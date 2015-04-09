using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SignalFxBackend.Configuration
{

    public class SignalFxBackendConfiguration
    {
        public string ApiToken { get; set; }
        public IDictionary<string, string> DefaultDimensions { get; set; }
        public string BaseURI { get; set; }
        public string DefaultSource { get; set; }
        public int MaxBatchSize { get; set; }
        public TimeSpan MaxTimeBetweenBatches { get; set; }
        public TimeSpan RetryDelay { get; set; }
        public TimeSpan PostTimeout { get; set; }


        public SignalFxBackendConfiguration(string apiToken, IDictionary<string, string> defaultDimensions, string baseURI,
             int maxBatchSize, string defaultSource, TimeSpan maxTimeBetweenBatches,
            TimeSpan retryDelay, TimeSpan postTimeout)
        {
            this.ApiToken = apiToken;
            this.DefaultDimensions = defaultDimensions;
            this.BaseURI = baseURI;
            this.MaxBatchSize = maxBatchSize;
            this.DefaultSource = defaultSource;
            this.MaxTimeBetweenBatches = maxTimeBetweenBatches;
            this.RetryDelay = retryDelay;
            this.PostTimeout = postTimeout;
        }

    }
}
