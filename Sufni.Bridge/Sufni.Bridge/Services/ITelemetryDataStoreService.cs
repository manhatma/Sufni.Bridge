using Sufni.Bridge.Models;
using System.Collections.ObjectModel;

namespace Sufni.Bridge.Services;

public interface ITelemetryDataStoreService
{
    public const string ServiceType = "_gosst._tcp";
    public ObservableCollection<ITelemetryDataStore> DataStores { get; }
}
