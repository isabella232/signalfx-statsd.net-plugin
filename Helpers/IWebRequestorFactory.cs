//
// Copyright (C) 2015 SignalFx, Inc.
//

namespace SignalFxBackend.Helpers
{
    /// <summary>
    /// A factory for creating IWebRequestor instances
    /// </summary>
    public interface IWebRequestorFactory
    {
        IWebRequestor GetRequestor();
    }
}
