//
// Copyright (C) 2015 SignalFx, Inc.
//

using statsd.net.core.Structures;
using System;
using System.Collections.Generic;
using System.Text;
namespace SignalFxBackend
{

    class Datapoint
    {
        private static char[] COMMA_SEP = new char[] { ',' };
        private static char[] EQUALS_SEP = new char[] { '=' };
        private static char[] OPEN_BRACE_SEP = new char[] { '[' };
        public string metric { get; set; }
        public double value { get; set; }
        public Dictionary<string, string> dimensions { get; set; }
        public Datapoint(string metric, double value, MetricTags tags, string postfix = "")
        {
            parseMetric(metric, postfix, tags);
            this.value = value;
        }

        private void parseMetric(string metric, string postfix, MetricTags tags)
        {
            dimensions = new Dictionary<string, string>();

            foreach (string tag in tags)
            {
                var nameValue = ParseTag(tag);
                dimensions[nameValue.Item1] = nameValue.Item2;
            }

            if (!metric.Contains("[") || !metric.EndsWith("]"))
            {
                this.metric = metric;
                return;
            }

            string[] metricKeyVals = metric.Split(OPEN_BRACE_SEP, 2);
            this.metric = metricKeyVals[0] + postfix;
            string keyVals = metricKeyVals[1].Substring(0, metricKeyVals[1].Length - 1);
            foreach (string possiblePair in keyVals.Split(COMMA_SEP))
            {
                string[] possibleKeyValue = possiblePair.Split(EQUALS_SEP, 2);
                if (possibleKeyValue.Length == 2)
                {
                    dimensions.Add(possibleKeyValue[0], possibleKeyValue[1]);
                }
            }

        }

        private Tuple<string, string> ParseTag(string tag)
        {
            StringBuilder operand = new StringBuilder();

            string left;
            bool escape = false;

            var i = 0;
            for (; i < tag.Length; ++i)
            {
                var chr = tag[i];
                if (!escape)
                {
                    if (chr == '\\')
                    {
                        escape = true;
                    }
                    else
                    {
                        if (chr == '=')
                        {
                            ++i;
                            break;
                        }
                        operand.Append(chr);
                    }
                }
                else
                {
                    escape = false;
                    operand.Append(chr);
                }
            }

            escape = false;
            left = operand.ToString();
            operand.Clear();

            for (; i < tag.Length; ++i)
            {
                var chr = tag[i];
                if (!escape)
                {
                    if (chr == '\\')
                    {
                        escape = true;
                    }
                    else
                    {
                        operand.Append(chr);
                    }
                }
                else
                {
                    operand.Append(chr);
                }
            }
            return new Tuple<string, string>(left, operand.ToString());
        }
    }
}