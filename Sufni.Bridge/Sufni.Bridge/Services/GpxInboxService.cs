using System;
using System.Collections.Generic;

namespace Sufni.Bridge.Services;

public sealed class GpxInboxService : IGpxInboxService
{
    private readonly List<string> pending = [];
    private EventHandler<string>? fileReceived;

    public event EventHandler<string>? FileReceived
    {
        add
        {
            fileReceived += value;
            if (value is null) return;
            string[] queued;
            lock (pending)
            {
                queued = pending.ToArray();
                pending.Clear();
            }

            foreach (var path in queued)
                value(this, path);
        }
        remove => fileReceived -= value;
    }

    public void NotifyFileReceived(string path)
    {
        var handler = fileReceived;
        if (handler is null)
        {
            lock (pending)
                pending.Add(path);
            return;
        }

        handler(this, path);
    }
}
