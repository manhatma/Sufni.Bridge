using System;

namespace Sufni.Bridge.Services;

public interface IGpxInboxService
{
    event EventHandler<string>? FileReceived;
    void NotifyFileReceived(string path);
}
