﻿
using Newtonsoft.Json;
using Opc.Ua;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpcPublisher
{
    using System.IO;
    using System.Linq;
    using System.Threading;
    using static OpcMonitoredItem;
    using static OpcPublisher.Workarounds.TraceWorkaround;
    using static OpcStackConfiguration;

    public class PublisherNodeConfiguration
    {
        public static SemaphoreSlim PublisherNodeConfigurationSemaphore = new SemaphoreSlim(1);
        public static List<OpcSession> OpcSessions = new List<OpcSession>();
        public static SemaphoreSlim OpcSessionsListSemaphore = new SemaphoreSlim(1);

        public static string PublisherNodeConfigurationFilename
        {
            get => _publisherNodeConfigurationFilename;
            set => _publisherNodeConfigurationFilename = value;
        }
        private static string _publisherNodeConfigurationFilename = $"{System.IO.Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}publishednodes.json";

        private List<NodePublishingConfiguration> _nodePublishingConfiguration;
        private static List<PublisherConfigurationFileEntry> _configurationFileEntries = new List<PublisherConfigurationFileEntry>();

        public PublisherNodeConfiguration()
        {
            _nodePublishingConfiguration = new List<NodePublishingConfiguration>();
        }

        /// <summary>
        /// Read and parse the publisher node configuration file.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> ReadConfigAsync()
        {
            // get information on the nodes to publish and validate the json by deserializing it.
            try
            {
                await PublisherNodeConfigurationSemaphore.WaitAsync();
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_GW_PNFP")))
                {
                    Trace("Publishing node configuration file path read from environment.");
                    _publisherNodeConfigurationFilename = Environment.GetEnvironmentVariable("_GW_PNFP");
                }

                Trace($"Attempting to load nodes file from: {_publisherNodeConfigurationFilename}");
                _configurationFileEntries = JsonConvert.DeserializeObject<List<PublisherConfigurationFileEntry>>(File.ReadAllText(_publisherNodeConfigurationFilename));
                Trace($"Loaded {_configurationFileEntries.Count} config file entry/entries.");

                foreach (var publisherConfigFileEntry in _configurationFileEntries)
                {
                    if (publisherConfigFileEntry.NodeId == null)
                    {
                        // new node configuration syntax.
                        foreach (var opcNode in publisherConfigFileEntry.OpcNodes)
                        {
                            ExpandedNodeId expandedNodeId = ExpandedNodeId.Parse(opcNode.ExpandedNodeId);
                            _nodePublishingConfiguration.Add(new NodePublishingConfiguration(expandedNodeId, publisherConfigFileEntry.EndpointUri, opcNode.OpcSamplingInterval ?? OpcSamplingInterval, opcNode.OpcPublishingInterval ?? OpcPublishingInterval));
                        }
                    }
                    else
                    {
                        // NodeId (ns=) format node configuration syntax using default sampling and publishing interval.
                        _nodePublishingConfiguration.Add(new NodePublishingConfiguration(publisherConfigFileEntry.NodeId, publisherConfigFileEntry.EndpointUri, OpcSamplingInterval, OpcPublishingInterval));
                        // give user a warning that the syntax is obsolete
                        Trace($"Please update the syntax of the configuration file and use ExpandedNodeId instead of NodeId property name for node with identifier '{publisherConfigFileEntry.NodeId.ToString()}' on EndpointUrl '{publisherConfigFileEntry.EndpointUri.AbsoluteUri}'.");

                    }
                }
            }
            catch (Exception e)
            {
                Trace(e, "Loading of the node configuration file failed. Does the file exist and has correct syntax?");
                Trace("exiting...");
                return false;
            }
            finally
            {
                PublisherNodeConfigurationSemaphore.Release();
            }
            Trace($"There are {_nodePublishingConfiguration.Count.ToString()} nodes to publish.");
            return true;
        }

        /// <summary>
        /// Create the publisher data structures to manage OPC sessions, subscriptions and monitored items.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> CreateOpcPublishingDataAsync()
        {
            // create a list to manage sessions, subscriptions and monitored items.
            try
            {
                await PublisherNodeConfigurationSemaphore.WaitAsync();
                await OpcSessionsListSemaphore.WaitAsync();

                var uniqueEndpointUris = _nodePublishingConfiguration.Select(n => n.EndpointUri).Distinct();
                foreach (var endpointUri in uniqueEndpointUris)
                {
                    // create new session info.
                    OpcSession opcSession = new OpcSession(endpointUri, OpcSessionCreationTimeout);

                    // create a subscription for each distinct publishing inverval
                    var nodesDistinctPublishingInterval = _nodePublishingConfiguration.Where(n => n.EndpointUri.AbsoluteUri.Equals(endpointUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)).Select(c => c.OpcPublishingInterval).Distinct();
                    foreach (var nodeDistinctPublishingInterval in nodesDistinctPublishingInterval)
                    {
                        // create a subscription for the publishing interval and add it to the session.
                        OpcSubscription opcSubscription = new OpcSubscription(nodeDistinctPublishingInterval);

                        // add all nodes with this OPC publishing interval to this subscription.
                        var nodesWithSamePublishingInterval = _nodePublishingConfiguration.Where(n => n.EndpointUri.AbsoluteUri.Equals(endpointUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)).Where(n => n.OpcPublishingInterval == nodeDistinctPublishingInterval);
                        foreach (var nodeInfo in nodesWithSamePublishingInterval)
                        {
                            // differentiate if NodeId or ExpandedNodeId format is used
                            if (nodeInfo.NodeId == null)
                            {
                                // create a monitored item for the node, we do not have the namespace index without a connected session. 
                                // so request a namespace update.
                                OpcMonitoredItem opcMonitoredItem = new OpcMonitoredItem(nodeInfo.ExpandedNodeId, opcSession.EndpointUri, true)
                                {
                                    RequestedSamplingInterval = nodeInfo.OpcSamplingInterval,
                                    SamplingInterval = nodeInfo.OpcSamplingInterval
                                };
                                opcSubscription.OpcMonitoredItems.Add(opcMonitoredItem);
                            }
                            else
                            {
                                // create a monitored item for the node with the configured or default sampling interval
                                OpcMonitoredItem opcMonitoredItem = new OpcMonitoredItem(nodeInfo.NodeId, opcSession.EndpointUri)
                                {
                                    RequestedSamplingInterval = nodeInfo.OpcSamplingInterval,
                                    SamplingInterval = nodeInfo.OpcSamplingInterval
                                };
                                opcSubscription.OpcMonitoredItems.Add(opcMonitoredItem);
                            }
                        }

                        // add subscription to session.
                        opcSession.OpcSubscriptions.Add(opcSubscription);
                    }

                    // add session.
                    OpcSessions.Add(opcSession);
                }
            }
            catch (Exception e)
            {
                Trace(e, "Creation of the internal OPC data managment structures failed.");
                Trace("exiting...");
                return false;
            }
            finally
            {
                OpcSessionsListSemaphore.Release();
                PublisherNodeConfigurationSemaphore.Release();
            }
            return true;
        }

        /// <summary>
        /// Returns a list of all published nodes for a specific endpoint in config file format.
        /// </summary>
        /// <returns></returns>
        public static async Task<List<PublisherConfigurationFileEntry>> GetPublisherConfigurationFileEntries(Uri endpointUri, OpcMonitoredItemConfigurationType? requestedType, bool getAll)
        {
            List<PublisherConfigurationFileEntry> publisherConfigurationFileEntries = new List<PublisherConfigurationFileEntry>();
            try
            {
                await PublisherNodeConfigurationSemaphore.WaitAsync();

                // itereate through all sessions, subscriptions and monitored items and create config file entries
                foreach (var session in OpcSessions)
                {
                    if (endpointUri == null || session.EndpointUri.AbsoluteUri.Equals(endpointUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    {
                        PublisherConfigurationFileEntry publisherConfigurationFileEntry = new PublisherConfigurationFileEntry();

                        publisherConfigurationFileEntry.EndpointUri = session.EndpointUri;
                        publisherConfigurationFileEntry.NodeId = null;
                        publisherConfigurationFileEntry.OpcNodes = null;
                        foreach (var subscription in session.OpcSubscriptions)
                        {
                            foreach (var monitoredItem in subscription.OpcMonitoredItems)
                            {
                                // ignore items tagged to stop
                                if (monitoredItem.State != OpcMonitoredItemState.RemovalRequested || getAll == true)
                                {
                                    OpcNodeOnEndpointUrl opcNodeOnEndpointUrl = new OpcNodeOnEndpointUrl();
                                    if (monitoredItem.ConfigType == OpcMonitoredItemConfigurationType.ExpandedNodeId)
                                    {
                                        // for certain scenarios we support returning the NodeId format even so the
                                        // actual configuration of the node was in ExpandedNodeId format
                                        if (requestedType == OpcMonitoredItemConfigurationType.NodeId)
                                        {
                                            PublisherConfigurationFileEntry legacyPublisherConfigFileEntry = new PublisherConfigurationFileEntry();
                                            legacyPublisherConfigFileEntry.EndpointUri = session.EndpointUri;
                                            legacyPublisherConfigFileEntry.NodeId = new NodeId(monitoredItem.ConfigExpandedNodeId.Identifier, (ushort)session.GetNamespaceIndex(monitoredItem.ConfigExpandedNodeId?.NamespaceUri));
                                            publisherConfigurationFileEntries.Add(legacyPublisherConfigFileEntry);
                                        }
                                        else
                                        {
                                            opcNodeOnEndpointUrl.ExpandedNodeId = monitoredItem.ConfigExpandedNodeId.ToString();
                                            opcNodeOnEndpointUrl.OpcPublishingInterval = (int)subscription.RequestedPublishingInterval;
                                            opcNodeOnEndpointUrl.OpcSamplingInterval = monitoredItem.RequestedSamplingInterval;
                                            if (publisherConfigurationFileEntry.OpcNodes == null)
                                            {
                                                publisherConfigurationFileEntry.OpcNodes = new List<OpcNodeOnEndpointUrl>();
                                            }
                                            publisherConfigurationFileEntry.OpcNodes.Add(opcNodeOnEndpointUrl);
                                        }
                                    }
                                    else
                                    {
                                        // we do not convert nodes with legacy configuration to the new format to keep backward
                                        // compatibility with external configurations.
                                        // the conversion would only be possible, if the session is connected, to have access to the
                                        // server namespace array.
                                        PublisherConfigurationFileEntry legacyPublisherConfigFileEntry = new PublisherConfigurationFileEntry();
                                        legacyPublisherConfigFileEntry.EndpointUri = session.EndpointUri;
                                        legacyPublisherConfigFileEntry.NodeId = monitoredItem.ConfigNodeId;
                                        publisherConfigurationFileEntries.Add(legacyPublisherConfigFileEntry);
                                    }
                                }
                            }
                        }
                        if (publisherConfigurationFileEntry.OpcNodes != null)
                        {
                            publisherConfigurationFileEntries.Add(publisherConfigurationFileEntry);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Trace(e, "Creation of configuration file entries failed.");
            }
            finally
            {
                PublisherNodeConfigurationSemaphore.Release();
            }
            return publisherConfigurationFileEntries;
        }

        /// <summary>
        /// Updates the configuration file to persist all currently published nodes
        /// </summary>
        public static async Task UpdateNodeConfigurationFile()
        {
            try
            {
                // itereate through all sessions, subscriptions and monitored items and create config file entries
                List<PublisherConfigurationFileEntry> publisherNodeConfiguration = await GetPublisherConfigurationFileEntries(null, null, true);

                // update the config file
                File.WriteAllText(PublisherNodeConfigurationFilename, JsonConvert.SerializeObject(publisherNodeConfiguration, Formatting.Indented));
            }
            catch (Exception e)
            {
                Trace(e, "Update of node configuration file failed.");
            }
        }
    }

    /// <summary>
    /// Class describing a list of nodes in the ExpandedNodeId format
    /// </summary>
    public class OpcNodeOnEndpointUrl
    {
        public string ExpandedNodeId;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? OpcSamplingInterval;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? OpcPublishingInterval;
    }

    /// <summary>
    /// Class describing the nodes which should be published. It supports three formats:
    /// - NodeId syntax using the namespace index (ns) syntax
    /// - ExpandedNodeId syntax, using the namespace URI (nsu) syntax
    /// - List of ExpandedNodeId syntax, to allow putting nodes with similar publishing and/or sampling intervals in one object
    /// </summary>
    public partial class PublisherConfigurationFileEntry
    {
        public PublisherConfigurationFileEntry()
        {
        }

        public PublisherConfigurationFileEntry(string nodeId, string endpointUrl)
        {
            NodeId = new NodeId(nodeId);
            EndpointUri = new Uri(endpointUrl);
        }

        [JsonProperty("EndpointUrl")]
        public Uri EndpointUri;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public NodeId NodeId;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<OpcNodeOnEndpointUrl> OpcNodes;
    }

    /// <summary>
    /// Describes the publishing information of a node.
    /// </summary>
    public class NodePublishingConfiguration
    {
        public Uri EndpointUri;
        public NodeId NodeId;
        public ExpandedNodeId ExpandedNodeId;
        public int OpcSamplingInterval;
        public int OpcPublishingInterval;

        public NodePublishingConfiguration(NodeId nodeId, Uri endpointUri, int opcSamplingInterval, int opcPublishingInterval)
        {
            NodeId = nodeId;
            ExpandedNodeId = null;
            EndpointUri = endpointUri;
            OpcSamplingInterval = opcSamplingInterval;
            OpcPublishingInterval = opcPublishingInterval;
        }
        public NodePublishingConfiguration(ExpandedNodeId expandedNodeId, Uri endpointUri, int opcSamplingInterval, int opcPublishingInterval)
        {
            NodeId = null;
            ExpandedNodeId = expandedNodeId;
            EndpointUri = endpointUri;
            OpcSamplingInterval = opcSamplingInterval;
            OpcPublishingInterval = opcPublishingInterval;
        }
    }
}
