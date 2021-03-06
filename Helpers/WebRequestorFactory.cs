//
// Copyright (C) 2015 SignalFx, Inc.
//

using System;
using System.Collections.Generic;

namespace SignalFxBackend.Helpers
{
    public class WebRequestorFactory : IWebRequestorFactory
    {
        public WebRequestorFactory()
        {
            _headers = new List<KeyValuePair<string, string>>();
        }

        public WebRequestorFactory WithUri(string uri)
        {
            _uri = uri;
            return this;
        }

        public WebRequestorFactory WithMethod(string method)
        {
            _method = method;
            return this;
        }

        public WebRequestorFactory WithContentType(string contentType)
        {
            _contentType = contentType;
            return this;
        }

        public WebRequestorFactory WithHeader(string header, string headerValue)
        {
            _headers.Add(new KeyValuePair<string, string>(header, headerValue));
            return this;
        }

        public IWebRequestorFactory WithTimeout(TimeSpan timeout)
        {
            _timeout = timeout;
            return this;
        }

        public IWebRequestor GetRequestor()
        {
            var req = new WebRequestor(_uri)
                .WithMethod(_method)
                .WithContentType(_contentType);
            if (_timeout.TotalMilliseconds > 0)
            {
                req = req.WithTimeout((int)_timeout.TotalMilliseconds);
            }

            foreach (var header in _headers)
            {
                req.WithHeader(header.Key, header.Value);
            }

            return req;
        }

        private string _uri;
        private string _method;
        private string _contentType;
        private List<KeyValuePair<string, string>> _headers;
        private TimeSpan _timeout;
    }
}
