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


namespace SignalFuseBackend
{
    [Export(typeof(IBackend))]
    public class SignalFuseBackend : IBackend
    {
        public const string DEFAULT_API_URL = "https://api.signalfuse.com";
        public bool IsActive { get; private set; }
        private Task _completionTask;
        private ILog _log;
        private string _source;
        private string _token;
        private RestClient _client;
        private ActionBlock<Datapoint> _outputBlock;

        public string Name { get { return "Signalfx"; } }

        public void Configure(string collectorName, XElement configElement, ISystemMetricsService systemMetrics)
        {

            _completionTask = new Task(() => IsActive = false);
            _log = SuperCheapIOC.Resolve<ILog>();

            var token = configElement.Attribute("token");
            _token = token.Value;
            _outputBlock = new ActionBlock<Datapoint>(datapoint => ProcessDatapoint(datapoint), Utility.OneAtATimeExecution());
            var apiURL = DEFAULT_API_URL;
            if (configElement.Attribute("api_url") != null)
            {
                apiURL = configElement.Attribute("api_url").Value;
            }
            _client = new RestClient(apiURL);
            _client.UserAgent = "statsd.net-signalfx-backend/" + Assembly.GetEntryAssembly().GetName().Version.ToString();
            IsActive = true;
            var executedCallBack = new AutoResetEvent(false);
            getInstance(executedCallBack);
            executedCallBack.WaitOne();
        }

        public DataflowMessageStatus OfferMessage(DataflowMessageHeader messageHeader, Bucket bucket, ISourceBlock<Bucket> source, bool consumeToAccept)
        {
            if (bucket.RootNamespace == "statsdnet.statsdnet.")
            {
                return DataflowMessageStatus.Accepted;
            }

            switch (bucket.BucketType)
            {
                case BucketType.Count:
                    var counterBucket = bucket as CounterBucket;
                    foreach (var counter in counterBucket.Items)
                    {
                        _outputBlock.Post(new Datapoint(counterBucket.RootNamespace + counter.Key, counter.Value, this._source));
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}:{1}|c", counter.Key, counter.Value));
                    }
                    break;
                case BucketType.Gauge:
                    var gaugeBucket = bucket as GaugesBucket;
                    foreach (var gauge in gaugeBucket.Gauges)
                    {
                        _outputBlock.Post(new Datapoint(gaugeBucket.RootNamespace + gauge.Key, gauge.Value, this._source));
                    }
                    break;
                case BucketType.Percentile:
                    var percentileBucket = bucket as PercentileBucket;
                    double percentileValue;
                    foreach (var pair in percentileBucket.Timings)
                    {
                        if (percentileBucket.TryComputePercentile(pair, out percentileValue))
                        {
                            _outputBlock.Post(new Datapoint(percentileBucket.RootNamespace + pair.Key + percentileBucket.PercentileName,
                              percentileValue, this._source));
                        }
                    }
                    break;
                case BucketType.Timing:
                    var timingBucket = bucket as LatencyBucket;
                    foreach (var timing in timingBucket.Latencies)
                    {

                    }
                    break;
            }

            return DataflowMessageStatus.Accepted;
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

        private void ProcessDatapoint(Datapoint dp)
        {
            var request = new RestRequest("/datapoint/").AddHeader("X-SF-TOKEN", this._token);
            request.RequestFormat = DataFormat.Json;
            request = request.AddBody(dp);
            var response = _client.Post(request);
            if (response.ErrorException != null)
            {

            }
            else
            {
                _log.Info("sent " + dp);
            }
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
    }
}
