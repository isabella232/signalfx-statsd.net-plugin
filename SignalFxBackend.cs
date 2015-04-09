//
// Copyright (C) 2015 SignalFx, Inc.
//

using com.signalfx.metrics.protobuf;
using log4net;
using SignalFxBackend.Configuration;
using SignalFxBackend.Helpers;
using statsd.net.core;
using statsd.net.core.Backends;
using statsd.net.core.Structures;
using statsd.net.shared;
using statsd.net.shared.Structures;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Xml.Linq;


namespace SignalFxBackend
{
    [Export(typeof(IBackend))]
    public class SignalFxBackend : IBackend
    {
        private static readonly string DEFAULT_URI = "https://ingest.signalfx.com";
        private static readonly int MAX_DATAPOINTS_PER_MESSAGE = 10000;
        private static readonly string INSTANCE_ID_DIMENSION = "InstanceId";
        private static readonly string METRIC_DIMENSION = "metric";
        private static readonly string SOURCE_DIMENSION = "source";
        private static readonly string SF_SOURCE = "sf_source";
        private static readonly HashSet<string> IGNORE_DIMENSIONS = new HashSet<string>();
        static SignalFxBackend()
        {
            IGNORE_DIMENSIONS.Add(SOURCE_DIMENSION);
            IGNORE_DIMENSIONS.Add(METRIC_DIMENSION);
        }
        public bool IsActive { get; private set; }
        private Task _completionTask;
        private ILog _log;
        private ISignalFxReporter _reporter;
        private SignalFxBackendConfiguration _config;
        private ActionBlock<Bucket> _processBlock;
        private BatchBlock<TypeDatapoint> _batchBlock;
        private ActionBlock<TypeDatapoint[]> _outputBlock;
        private Timer _triggerBatchTimer;

        public string Name { get { return "SignalFx"; } }

        public void Configure(string collectorName, XElement configElement, ISystemMetricsService systemMetrics)
        {

            _completionTask = new Task(() => IsActive = false);
            _log = SuperCheapIOC.Resolve<ILog>();


            _config = parseConfig(configElement);
            _reporter = new SignalFxReporter(_config.BaseURI, _config.ApiToken, _config.PostTimeout);

            _processBlock = new ActionBlock<Bucket>(bucket => ProcessBucket(bucket), Utility.UnboundedExecution());
            _batchBlock = new BatchBlock<TypeDatapoint>(_config.MaxBatchSize);
            _outputBlock = new ActionBlock<TypeDatapoint[]>(datapoint => ProcessDatapoints(datapoint), Utility.OneAtATimeExecution());
            _batchBlock.LinkTo(_outputBlock);

            _triggerBatchTimer = new Timer((state) => trigger(state), null, TimeSpan.FromSeconds(1), _config.MaxTimeBetweenBatches);
            IsActive = true;
        }

        private void trigger(object state)
        {
            _batchBlock.TriggerBatch();
        }

        private SignalFxBackendConfiguration parseConfig(XElement configElement)
        {
            var apiToken = getRequiredStringConfig(configElement, "apiToken");
            var defaultDimensionsNode = configElement.Element("defaultDimensions");
            IDictionary<string, string> defaultDimensions = new Dictionary<string, string>();
            if (defaultDimensionsNode != null)
            {
                foreach (var defaultDimensionNode in defaultDimensionsNode.Elements("defaultDimension"))
                {
                    string name = getRequiredStringConfig(defaultDimensionNode, "name");
                    string value = getRequiredStringConfig(defaultDimensionNode, "value");
                    if (!String.IsNullOrEmpty(name) && !String.IsNullOrEmpty(value))
                    {
                        defaultDimensions[name] = value;
                    }
                }
            }

            var awsIntegrationNode = configElement.Attribute("awsIntegration");
            if (awsIntegrationNode != null && configElement.ToBoolean("awsIntegration"))
            {
                var awsRequestor = new WebRequestor("http://169.254.169.254/latest/meta-data/instance-id")
                        .WithTimeout(1000 * 60)
                        .WithMethod("GET");

                using (var resp = awsRequestor.Send())
                {
                    defaultDimensions[INSTANCE_ID_DIMENSION] = new StreamReader(resp).ReadToEnd();
                }
            }

            var baseURI = getOptionalStringConfig(configElement, "baseURI", DEFAULT_URI);

            var sourceType = getRequiredStringConfig(configElement, "sourceType");
            string source;
            switch (sourceType)
            {
                case "netbios":
                    source = System.Environment.MachineName;
                    break;
                case "dns":
                    source = System.Net.Dns.GetHostName();
                    break;
                case "fqdn":
                    string domainName = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
                    string hostName = System.Net.Dns.GetHostName();

                    if (!hostName.EndsWith(domainName))  // if hostname does not already include domain name
                    {
                        hostName += "." + domainName;   // add the domain name part
                    }
                    source = hostName;
                    break;
                case "custom":
                    source = getRequiredStringConfig(configElement, "sourceValue", " when \"sourceType\" is \"custom\"");
                    break;
                default:
                    throw new Exception("sourceType attribute must be one of netbios, dns, fqdn, or custom(with source attribute set) on " + configElement);
            }

            var maxBatchSize = getOptionalIntConfig(configElement, "maxBatchSize", MAX_DATAPOINTS_PER_MESSAGE);
            var maxTimeBetweenBatches = getOptionalTimeConfig(configElement, "maxWaitBetweenBatches", Utility.ConvertToTimespan("5s"));
            var retryDelay = getOptionalTimeConfig(configElement, "retryDelay", Utility.ConvertToTimespan("1s"));
            var postTimeout = getOptionalTimeConfig(configElement, "postTimeout", Utility.ConvertToTimespan("1s"));
            return new SignalFxBackendConfiguration(apiToken, defaultDimensions, baseURI, maxBatchSize, source,
                maxTimeBetweenBatches, retryDelay, postTimeout);
        }

        private TimeSpan getOptionalTimeConfig(XElement configElement, string configName, TimeSpan defaultValue)
        {
            var attr = configElement.Attribute(configName);
            return attr != null ? Utility.ConvertToTimespan(attr.Value) : defaultValue;

        }

        private String getRequiredStringConfig(XElement configElement, string configName, string errMsg = "")
        {
            var attr = configElement.Attribute(configName);
            if (attr == null)
            {
                throw new Exception(configName + " attribute is required on " + configElement.Name + errMsg);
            }
            return attr.Value;
        }

        private String getOptionalStringConfig(XElement configElement, string configName, string defaultValue)
        {
            var attr = configElement.Attribute(configName);
            return attr != null ? attr.Value : defaultValue;
        }

        private int getOptionalIntConfig(XElement configElement, string configName, int defaultValue)
        {
            var attr = configElement.Attribute(configName);
            return attr != null ? int.Parse(attr.Value) : defaultValue;
        }


        public DataflowMessageStatus OfferMessage(DataflowMessageHeader messageHeader, Bucket bucket, ISourceBlock<Bucket> source, bool consumeToAccept)
        {
            if (bucket.RootNamespace != "statsdnet.statsdnet.")
            {
                _processBlock.Post(bucket);
            }
            return DataflowMessageStatus.Accepted;
        }

        private void ProcessBucket(Bucket bucket)
        {
            switch (bucket.BucketType)
            {
                case BucketType.Count:
                    var counterBucket = bucket as CounterBucket;
                    foreach (var counter in counterBucket.Items)
                    {
                        _batchBlock.Post(new TypeDatapoint(MetricType.COUNTER, new Datapoint(counterBucket.RootNamespace + counter.Key.Name, counter.Value, counter.Key.Tags)));
                    }
                    break;
                case BucketType.Gauge:
                    var gaugeBucket = bucket as GaugesBucket;
                    foreach (var gauge in gaugeBucket.Gauges)
                    {
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(gaugeBucket.RootNamespace + gauge.Key.Name, gauge.Value, gauge.Key.Tags)));
                    }
                    break;
                case BucketType.Percentile:
                    var percentileBucket = bucket as PercentileBucket;
                    double percentileValue;
                    foreach (var pair in percentileBucket.Timings)
                    {
                        if (percentileBucket.TryComputePercentile(pair, out percentileValue))
                        {
                            _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(percentileBucket.RootNamespace + pair.Key.Name,
                              percentileValue, pair.Key.Tags, percentileBucket.PercentileName)));
                        }
                    }
                    break;
                case BucketType.Timing:
                    var timingBucket = bucket as LatencyBucket;
                    foreach (var latency in timingBucket.Latencies)
                    {
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key.Name, latency.Value.Count, latency.Key.Tags, ".count")));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key.Name, latency.Value.Min,  latency.Key.Tags, ".min")));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key.Name, latency.Value.Max,  latency.Key.Tags, ".max")));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key.Name, latency.Value.Mean,  latency.Key.Tags, ".mean")));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key.Name, latency.Value.Sum,  latency.Key.Tags, ".sum")));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key.Name, latency.Value.SumSquares,  latency.Key.Tags, ".sumSquares")));
                    }
                    break;
            }
        }

        public void Complete()
        {
            _completionTask.Start();
        }

        public Task Completion
        {
            get { return _completionTask; }
        }

        public void Fault(Exception exception)
        {
            throw new NotImplementedException();
        }

        public int OutputCount
        {
            get { return 0; }
        }

        private void ProcessDatapoints(TypeDatapoint[] typeDatapoints)
        {
            _triggerBatchTimer.Change(_config.MaxTimeBetweenBatches, _config.MaxTimeBetweenBatches);

            var msg = new DataPointUploadMessage();
            foreach (var typeDatapoint in typeDatapoints)
            {
                msg.datapoints.Add(typeDatapoint.toDataPoint(_config.DefaultDimensions, _config.DefaultSource));
            }

            _reporter.Send(msg);
            /*

             _retryPolicy.ExecuteAction(() =>
                 {
                     var result = _client.Execute(request);
                     if (result.StatusCode == HttpStatusCode.Unauthorized)
                     {
                         throw new UnauthorizedAccessException("SignalFuse reports that your access is not authorised. Is your API key correct?");
                     }
                     else if (result.StatusCode != HttpStatusCode.OK)
                     {
                         _log.Warn(String.Format("Request could not be processed. Server said {0}", result.StatusCode.ToString()));
                     }
                 });
            */
        }


        private class TypeDatapoint
        {
            public MetricType type { get; set; }
            public Datapoint data { get; set; }

            public TypeDatapoint(MetricType type, Datapoint data)
            {
                this.type = type;
                this.data = data;
            }

            public DataPoint toDataPoint(IDictionary<string, string> defaultDimensions, string defaultSource)
            {
                DataPoint dataPoint = new DataPoint();

                string metricName = data.dimensions.ContainsKey(METRIC_DIMENSION) ? data.dimensions[METRIC_DIMENSION] : data.metric;
                string sourceName = data.dimensions.ContainsKey(SOURCE_DIMENSION) ? data.dimensions[SOURCE_DIMENSION] : defaultSource;
                dataPoint.metric = metricName;
                dataPoint.metricType = type;
                dataPoint.source = sourceName;
                AddDimension(dataPoint, SF_SOURCE, sourceName);

                Datum datum = new Datum();
                datum.doubleValue = data.value;
                dataPoint.value = datum;
                AddDimensions(dataPoint, defaultDimensions);
                AddDimensions(dataPoint, data.dimensions);
                return dataPoint;
            }

            protected void AddDimensions(DataPoint dataPoint, IDictionary<string, string> dimensions)
            {
                foreach (KeyValuePair<string, string> entry in dimensions)
                {
                    if (!IGNORE_DIMENSIONS.Contains(entry.Key) && !string.IsNullOrEmpty(entry.Value))
                    {
                        AddDimension(dataPoint, entry.Key, entry.Value);
                    }
                }
            }
            protected virtual void AddDimension(DataPoint dataPoint, string key, string value)
            {
                Dimension dimension = new Dimension();
                dimension.key = key;
                dimension.value = value;
                dataPoint.dimensions.Add(dimension);
            }
        }
    }
}
