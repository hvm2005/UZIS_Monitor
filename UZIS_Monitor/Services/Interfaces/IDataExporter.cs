using UZIS_Monitor.Models;

namespace UZIS_Monitor.Services.Interfaces
{
    public interface IDataExporter : IFileFormatInfo
    {
        Task ExportAsync(IEnumerable<PacketData> data, string filePath);
    }
}
