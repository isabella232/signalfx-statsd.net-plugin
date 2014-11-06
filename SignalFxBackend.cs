/**
 * Copyright (C) 2014 SignalFuse, Inc.
 */
using Microsoft.Practices.TransientFaultHandling;
using RestSharp;
using statsd.net.core;
using statsd.net.core.Backends;
using statsd.net.core.Structures;
using statsd.net.shared;
using statsd.net.shared.Structures;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Xml.Linq;
using log4net;
using System.Threading;
using RestSharp.Serializers;
using System.Net;


namespace SignalFxBackend
{
    [Export(typeof(IBackend))]
    public class SignalFxBackend : IBackend
    {
        public const string DEFAULT_API_URL = "https://api.signalfuse.com";
        public bool IsActive { get; private set; }
        private Task _completionTask;
        private ILog _log;
        private string _source;
        private SignalFxBackendConfiguration _config;
        private RestClient _client;
        private ActionBlock<Bucket> _processBlock;
        private BatchBlock<TypeDatapoint> _batchBlock;
        private ActionBlock<TypeDatapoint[]> _outputBlock;
        private RetryPolicy<SignalFxErrorDetectionStrategy> _retryPolicy;
        private Incremental _retryStrategy;

        public string Name { get { return "Signalfx"; } }

        public void Configure(string collectorName, XElement configElement, ISystemMetricsService systemMetrics)
        {

            _completionTask = new Task(() => IsActive = false);
            _log = SuperCheapIOC.Resolve<ILog>();

            var config = new SignalFxBackendConfiguration(token: configElement.Attribute("token").Value,
                numRetries: configElement.ToInt("numRetries"),
                retryDelay: Utility.ConvertToTimespan(configElement.Attribute("retryDelay").Value),
                postTimeout: Utility.ConvertToTimespan(configElement.Attribute("postTimeout").Value),
                maxBatchSize: configElement.ToInt("maxBatchSize")
                );
            _config = config;

            _processBlock = new ActionBlock<Bucket>(bucket => ProcessBucket(bucket), Utility.UnboundedExecution());
            _batchBlock = new BatchBlock<TypeDatapoint>(config.MaxBatchSize);
            _outputBlock = new ActionBlock<TypeDatapoint[]>(datapoint => ProcessDatapoints(datapoint), Utility.OneAtATimeExecution());
            _batchBlock.LinkTo(_outputBlock);

            var apiURL = DEFAULT_API_URL;
            if (configElement.Attribute("api_url") != null)
            {
                apiURL = configElement.Attribute("api_url").Value;
            }
            _client = new RestClient(apiURL);
            _client.UserAgent = "statsd.net-signalfx-backend/" + Assembly.GetEntryAssembly().GetName().Version.ToString();
            _client.Timeout = (int)_config.PostTimeout.TotalMilliseconds;

            _retryPolicy = new RetryPolicy<SignalFxErrorDetectionStrategy>(_config.NumRetries);
            _retryPolicy.Retrying += (sender, args) =>
            {
                _log.Warn(String.Format("Retry {0} failed. Trying again. Delay {1}, Error: {2}", args.CurrentRetryCount, args.Delay, args.LastException.Message), args.LastException);
            };
            _retryStrategy = new Incremental(_config.NumRetries, _config.RetryDelay, TimeSpan.FromSeconds(2));

            if (configElement.Attribute("source") == null || configElement.Attribute("source").Value.Length == 0)
            {
                var executedCallBack = new AutoResetEvent(false);
                getInstance(executedCallBack);
                executedCallBack.WaitOne();
            }
            else
            {
                this._source = configElement.Attribute("source").Value;
            }
            IsActive = true;
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
                        _batchBlock.Post(new TypeDatapoint(MetricType.COUNTER, new Datapoint(counterBucket.RootNamespace + counter.Key, counter.Value, this._source)));
                    }
                    break;
                case BucketType.Gauge:
                    var gaugeBucket = bucket as GaugesBucket;
                    foreach (var gauge in gaugeBucket.Gauges)
                    {
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(gaugeBucket.RootNamespace + gauge.Key, gauge.Value, this._source)));
                    }
                    break;
                case BucketType.Percentile:
                    var percentileBucket = bucket as PercentileBucket;
                    double percentileValue;
                    foreach (var pair in percentileBucket.Timings)
                    {
                        if (percentileBucket.TryComputePercentile(pair, out percentileValue))
                        {
                            _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(percentileBucket.RootNamespace + pair.Key + percentileBucket.PercentileName,
                              percentileValue, this._source)));
                        }
                    }
                    break;
                case BucketType.Timing:
                    var timingBucket = bucket as LatencyBucket;
                    foreach (var latency in timingBucket.Latencies)
                    {
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key + ".count", latency.Value.Count, this._source)));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key + ".min", latency.Value.Min, this._source)));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key + ".max", latency.Value.Max, this._source)));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key + ".mean", latency.Value.Mean, this._source)));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key + ".sum", latency.Value.Sum, this._source)));
                        _batchBlock.Post(new TypeDatapoint(MetricType.GAUGE, new Datapoint(timingBucket.RootNamespace + latency.Key + ".sumSquares", latency.Value.SumSquares, this._source)));
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
            var datapoints = new Datapoints();
            var stringbuilder = new StringBuilder();
            foreach (var typeDatapoint in typeDatapoints)
            {
                switch (typeDatapoint.type)
                {
                    case MetricType.GAUGE:
                        datapoints.gauge.Add(typeDatapoint.data);
                        break;
                    case MetricType.COUNTER:
                        datapoints.counter.Add(typeDatapoint.data);
                        break;
                }
            }

            stringbuilder.Append(SimpleJson.SerializeObject(datapoints));
            var request = new RestSharp.RestRequest("/v2/datapoint", RestSharp.Method.POST).AddHeader("X-SF-TOKEN", this._config.Token);
            request.AddParameter("", stringbuilder.ToString(), ParameterType.RequestBody);
            request.AddHeader("Content-Type", "application/json");

            _retryPolicy.ExecuteAction(() =>
                {
                    var result = _client.Execute(request);
                    if (result.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        throw new UnauthorizedAccessException("SignalFuse reports that your access is not authorised. Is your API key and email address correct?");
                    }
                    else if (result.StatusCode != HttpStatusCode.OK)
                    {
                      _log.Warn(String.Format("Request could not be processed. Server said {0}", result.StatusCode.ToString()));
                    }
                });
        }

        private async void getInstance(AutoResetEvent are)
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri("http://169.254.169.254");
                HttpResponseMessage response = await client.GetAsync("/latest/meta-data/instance-id");
                response.EnsureSuccessStatusCode();
                _source = await response.Content.ReadAsStringAsync();
                are.Set();
            }
        }

        private class SignalFxErrorDetectionStrategy : ITransientErrorDetectionStrategy
        {
            public bool IsTransient(Exception ex)
            {
                if (ex is TimeoutException)
                {
                    return true;
                }
                return false;
            }
        }

        private class SignalFxBackendConfiguration
        {
            public string Token { get; set; }
            public TimeSpan RetryDelay { get; set; }
            public TimeSpan PostTimeout { get; set; }
            public int MaxBatchSize { get; set; }
            public int NumRetries { get; set; }

            public SignalFxBackendConfiguration(string token, TimeSpan retryDelay, int numRetries, TimeSpan postTimeout, int maxBatchSize)
            {
                this.Token = token;
                this.RetryDelay = retryDelay;
                this.NumRetries = numRetries;
                this.PostTimeout = postTimeout;
                this.MaxBatchSize = maxBatchSize;
            }
        }

        public enum MetricType
        {
            GAUGE,
            COUNTER,
            ENUM,
            CUMULATIVE_COUNTER
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
        }

        private class Datapoints
        {
            public List<Datapoint> gauge { get; set; }
            public List<Datapoint> counter { get; set; }
            public List<Datapoint> cumulative_counter { get; set; }

            public Datapoints()
            {
                this.gauge = new List<Datapoint>();
                this.counter = new List<Datapoint>();
                this.cumulative_counter = new List<Datapoint>();
            }
        }
    }



}
