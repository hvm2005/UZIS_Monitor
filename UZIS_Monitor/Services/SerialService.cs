using RJCP.IO.Ports;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using UZIS_Monitor.Models;

namespace UZIS_Monitor.Services
{
    public class SerialService
    {
        // Каналы и настройки пакета
        private readonly Channel<PacketData> _packetChannel = Channel.CreateBounded<PacketData>(500);
        public ChannelReader<PacketData> PacketsReader => _packetChannel.Reader;

        private static readonly byte[] Header = "UZIS"u8.ToArray();
        private static readonly int PayloadSize = Marshal.SizeOf<PacketData>();
        private static readonly int TotalPacketSize = Header.Length + PayloadSize;

        // Ресурсы порта и пайплайнов
        private SerialPortStream? _comPort;
        private CancellationTokenSource? _serviceCts;

        // Состояние
        private bool _isClosing;
        private bool _isConnected;
        private DateTime _lastPacketTime = DateTime.UtcNow;

        //public event Action<string>? OnStatusChanged;
        public event Action<bool, string?>? OnConnectionStatusChanged;

        public SerialService()
        {
            Task.Run(ConnectionMonitorLoop);
        }

        private async Task ConnectionMonitorLoop()
        {
            while (!_isClosing)
            {
                if (!_isConnected)
                {
                    await TryAutoConnectAsync();
                }
                else if ((DateTime.UtcNow - _lastPacketTime).TotalMilliseconds > 500)
                {
                    //OnStatusChanged?.Invoke("Данные не поступают. Переподключение...");
                    Disconnect();
                }
                await Task.Delay(500);
            }
        }

        private async Task<bool> TryAutoConnectAsync(int baudRate = 921600)
        {
            string[] ports = SerialPortStream.GetPortNames();
            foreach (var portName in ports)
            {
                if (_isClosing) return false;
                //OnStatusChanged?.Invoke($"Проверка {portName}...");

                SerialPortStream? port = null;
                try
                {
                    port = new SerialPortStream(portName, baudRate) { ReadTimeout = 200 };
                    port.Open();

                    byte[] checkBuf = new byte[1024];
                    int read = await Task.Run(() => port.Read(checkBuf, 0, checkBuf.Length));

                    if (checkBuf.AsSpan(0, read).IndexOf(Header) != -1)
                    {
                        InitializeMainPort(port);
                        return true;
                    }
                    port.Dispose();
                }
                catch
                {
                    port?.Dispose();
                }
            }
            //OnStatusChanged?.Invoke("Устройство не найдено");
            return false;
        }

        private void InitializeMainPort(SerialPortStream openedPort)
        {
            _comPort = openedPort;
            _comPort.ReadTimeout = SerialPortStream.InfiniteTimeout;
            _comPort.ErrorReceived += (s, e) => Disconnect();

            _serviceCts = new CancellationTokenSource();
            var pipe = new Pipe();

            // Запуск конвейера: Чтение -> Парсинг
            _ = FillPipeAsync(_comPort, pipe.Writer, _serviceCts.Token);
            _ = ReadPipeAsync(pipe.Reader, _serviceCts.Token);

            SetConnectionStatus(true, _comPort.PortName);
            //OnStatusChanged?.Invoke($"Подключено: {_comPort.PortName}");
            _lastPacketTime = DateTime.UtcNow;
        }

        private async Task FillPipeAsync(Stream stream, PipeWriter writer, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Memory<byte> memory = writer.GetMemory(4096);
                    int bytesRead = await stream.ReadAsync(memory, ct);

                    if (bytesRead == 0) break;
                    writer.Advance(bytesRead);

                    FlushResult result = await writer.FlushAsync(ct);
                    if (result.IsCompleted) break;
                }
            }
            catch (Exception ex)
            {
                //if (!_isClosing) OnStatusChanged?.Invoke($"Ошибка порта: {ex.Message}");
            }
            finally
            {
                await writer.CompleteAsync();
                Disconnect();
            }
        }

        private async Task ReadPipeAsync(PipeReader reader, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ReadResult result = await reader.ReadAsync(ct);
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    // Извлекаем все доступные пакеты
                    while (TryParsePacket(ref buffer, out PacketData packet))
                    {
                        _lastPacketTime = DateTime.UtcNow;
                        _packetChannel.Writer.TryWrite(packet);
                    }

                    // Сообщаем Pipe, что мы потребили (Start) и до куда просмотрели (End)
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (result.IsCompleted) break;
                }
            }
            finally
            {
                await reader.CompleteAsync();
            }
        }

        private bool TryParsePacket(ref ReadOnlySequence<byte> buffer, out PacketData packet)
        {
            var reader = new SequenceReader<byte>(buffer);

            // 1. Ищем начало заголовка "UZIS"
            if (!reader.TryAdvanceTo(Header[0], advancePastDelimiter: false))
            {
                buffer = buffer.Slice(buffer.End); // Сбрасываем пустой буфер
                packet = default;
                return false;
            }

            // 2. Проверяем, хватает ли данных на заголовок + тело
            if (reader.Remaining < TotalPacketSize)
            {
                buffer = buffer.Slice(reader.Position); // Ждем докачки
                packet = default;
                return false;
            }

            // 3. Проверяем полный заголовок "UZIS"
            if (!CheckHeader(ref reader))
            {
                buffer = buffer.Slice(reader.Position).Slice(1); // Сдвиг на 1 и ищем дальше
                packet = default;
                return false;
            }

            // 4. Чтение Payload (Zero-copy)
            ReadOnlySequence<byte> payloadSeq = reader.UnreadSequence.Slice(0, PayloadSize);
            if (payloadSeq.IsSingleSegment)
            {
                packet = MemoryMarshal.Read<PacketData>(payloadSeq.First.Span);
            }
            else
            {
                Span<byte> temp = stackalloc byte[PayloadSize];
                payloadSeq.CopyTo(temp);
                packet = MemoryMarshal.Read<PacketData>(temp);
            }

            buffer = buffer.Slice(payloadSeq.End);
            return true;
        }

        private bool CheckHeader(ref SequenceReader<byte> reader)
        {
            // Сверяем оставшиеся байты заголовка
            for (int i = 0; i < Header.Length; i++)
            {
                if (!reader.TryRead(out byte b) || b != Header[i]) return false;
            }
            return true;
        }

        private void SetConnectionStatus(bool connected, string? portName)
        {
            if (_isConnected != connected)
            {
                _isConnected = connected;
                OnConnectionStatusChanged?.Invoke(connected, portName);
            }
        }

        private void Disconnect()
        {
            if (_comPort == null) return;

            _serviceCts?.Cancel();
            try { _comPort.Dispose(); } catch { }

            _comPort = null;
            SetConnectionStatus(false, null);
            //OnStatusChanged?.Invoke("Устройство отключено");
        }

        public void StopService()
        {
            _isClosing = true;
            Disconnect();
        }
    }
}
