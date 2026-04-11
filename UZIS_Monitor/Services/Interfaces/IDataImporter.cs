using UZIS_Monitor.Models;

namespace UZIS_Monitor.Services.Interfaces
{
    public interface IDataImporter : IFileFormatInfo
    {
        Task<List<PacketData>> ImportAsync(string filePath);
    }
}
