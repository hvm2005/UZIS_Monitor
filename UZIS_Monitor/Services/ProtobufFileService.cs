using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UZIS_Monitor.Models;
using UZIS_Monitor.Services.Interfaces;

namespace UZIS_Monitor.Services
{
    internal class ProtobufFileService : IDataExporter, IDataImporter, IFileFormatInfo
    {
        public string DisplayName => "Бинарный лог (Protobuf)";
        public string FileExtension => ".pbin";
        public string FileFilter => "UZIS Binary Data (*.pbin)|*.pbin";

        public async Task ExportAsync(IEnumerable<PacketData> data, string filePath)
        {
            await Task.Run(() =>
            {
                using var file = File.Create(filePath);
                foreach (var packet in data)
                {
                    ProtoBuf.Serializer.SerializeWithLengthPrefix(file, packet, ProtoBuf.PrefixStyle.Base128);
                }
            });
        }

        public async Task<List<PacketData>> ImportAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                using var file = File.OpenRead(filePath);

                // DeserializeItems возвращает IEnumerable, который лениво читает файл.
                // PrefixStyle.Base128 соответствует тому, как мы записывали (SerializeWithLengthPrefix).
                // ToList() сразу вычитает все пакеты в память.
                var items = ProtoBuf.Serializer.DeserializeItems<PacketData>(file, ProtoBuf.PrefixStyle.Base128, 0);

                return items.ToList();
            });
        }
    }
}
