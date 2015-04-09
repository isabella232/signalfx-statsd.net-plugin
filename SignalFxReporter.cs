//
// Copyright (C) 2015 SignalFx, Inc.
//

using com.signalfx.metrics.protobuf;
using log4net;
using ProtoBuf;
using SignalFxBackend.Helpers;
using statsd.net.shared;
using System;
using System.IO;
using System.Net;
using System.Security;

namespace SignalFxBackend
{
    public class SignalFxReporter : ISignalFxReporter
    {
        private readonly ILog _log;
        private string _apiToken;
        private IWebRequestorFactory _requestor;

        public SignalFxReporter(string baseURI, string apiToken, TimeSpan timeout, IWebRequestorFactory requestor = null)
        {
            _log = SuperCheapIOC.Resolve<ILog>();
            if (requestor == null)
            {
                requestor = new WebRequestorFactory()
                    .WithUri(baseURI + "/v2/datapoint")
                    .WithMethod("POST")
                    .WithContentType("application/x-protobuf")
                    .WithHeader("X-SF-TOKEN", apiToken)
                    .WithTimeout(timeout);
            }

            this._requestor = requestor;
            this._apiToken = apiToken;
        }

        public void Send(DataPointUploadMessage msg)
        {
            try
            {
                var request = _requestor.GetRequestor();
                using (var rs = request.GetWriteStream())
                {
                    Serializer.Serialize(rs, msg);
                    // flush the message before disposing
                    rs.Flush();
                }
                try
                {
                    using (request.Send())
                    {
                    }
                }
                catch (SecurityException)
                {
                    _log.Error("API token for sending metrics to SignalFuse is invalid");
                }
            }
            catch (Exception ex)
            {
                if (ex is WebException)
                {
                    var webex = ex as WebException;
                    using (var exresp = webex.Response)
                    {
                        if (exresp != null)
                        {
                            var stream2 = exresp.GetResponseStream();
                            var reader2 = new StreamReader(stream2);
                            var errorStr = reader2.ReadToEnd();
                            _log.Error(errorStr);
                        }
                    }
                }
                //MetricsErrorHandler.Handle(ex, "Failed to send metrics");
            }
        }
    }
}
