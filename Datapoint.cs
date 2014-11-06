/**
 * Copyright (C) 2014 SignalFuse, Inc.
 */

using System.Collections.Generic;
namespace SignalFxBackend
{

    class Datapoint
    {
        private static char[] COMMA_SEP = new char[] { ',' };
        private static char[] COLON_SEP = new char[] { '=' };
        private static char[] OPEN_BRACE_SEP = new char[] { '[' };
        public string metric { get; set; }
        public double value { get; set; }
        public Dictionary<string, string> dimensions { get; set; }
        public Datapoint(string metric, double value, string source)
        {
            parseMetric(metric, source);
            this.value = value;
        }

        private void parseMetric(string metric, string source)
        {
            this.dimensions = new Dictionary<string, string>();
            this.dimensions.Add("sf_source", source);
            if (!metric.Contains("[") || !metric.EndsWith("]"))
            {
                this.metric = metric;
                return;
            }

            string[] metricKeyVals = metric.Split(OPEN_BRACE_SEP, 2);
            this.metric = metricKeyVals[0];
            string keyVals = metricKeyVals[1].Substring(0, metricKeyVals[1].Length - 1);
            foreach (string possiblePair in keyVals.Split(COMMA_SEP))
            {
                string[] possibleKeyValue = possiblePair.Split(COLON_SEP);
                if (possibleKeyValue.Length == 2)
                {
                    dimensions.Add(possibleKeyValue[0], possibleKeyValue[1]);
                }
            }
        }
    }
}