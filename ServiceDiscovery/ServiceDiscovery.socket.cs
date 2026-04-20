using Tmds.MDns;

namespace ServiceDiscovery;

public class ServiceDiscovery : IServiceDiscovery
{
    public event EventHandler<ServiceAnnouncementEventArgs>? ServiceAdded;
    public event EventHandler<ServiceAnnouncementEventArgs>? ServiceRemoved;

    private readonly ServiceBrowser browser = new();

    private static int ParseProtocolVersion(IList<string>? txt)
    {
        if (txt is null) return 1;
        foreach (var entry in txt)
        {
            if (!entry.StartsWith("proto=", StringComparison.Ordinal)) continue;
            if (int.TryParse(entry.AsSpan(6), out var parsed)) return parsed;
        }
        return 1;
    }

    public ServiceDiscovery()
    {
        browser.ServiceAdded += (sender, args) =>
        {
            var announcement = new ServiceAnnouncement
            {
                Address = args.Announcement.Addresses[0],
                Port = args.Announcement.Port,
                ProtocolVersion = ParseProtocolVersion(args.Announcement.Txt),
            };
            ServiceAdded?.Invoke(sender, new ServiceAnnouncementEventArgs(announcement));
        };
        
        browser.ServiceRemoved += (sender, args) =>
        {
            var announcement = new ServiceAnnouncement
            {
                Address = args.Announcement.Addresses[0],
                Port = args.Announcement.Port,
            };
            ServiceRemoved?.Invoke(sender, new ServiceAnnouncementEventArgs(announcement));
        };
    }
    
    public void StartBrowse(string type)
    {
        browser.StartBrowse(type);
    }
}