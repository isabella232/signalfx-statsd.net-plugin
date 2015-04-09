//
// Copyright (C) 2015 SignalFx, Inc.
//

using System.IO;

namespace SignalFxBackend.Helpers
{
    public interface IWebRequestor
    {
        Stream GetWriteStream();

        Stream Send();
    }
}
