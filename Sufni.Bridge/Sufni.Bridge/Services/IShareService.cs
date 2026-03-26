using System.Threading.Tasks;

namespace Sufni.Bridge.Services;

public interface IShareService
{
    Task ShareFileAsync(string filePath);
}
