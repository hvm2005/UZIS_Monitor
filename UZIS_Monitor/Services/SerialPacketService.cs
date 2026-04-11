using System;
using System.Collections.Generic;
using RJCP.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using UZIS_Monitor.Models;

namespace UZIS_Monitor.Services
{
    public class SerialPacketService
    {
        private SerialPortStream? _comPort;
        private readonly List<byte> _buffer = new(4096);
        private static readonly byte[] Header = "UZIS"u8.ToArray();
        private static readonly int PayloadSize = Marshal.SizeOf<PacketData>();
        private static readonly int TotalPacketSize = Header.Length + PayloadSize;

        private bool _isClosing; // Флаг для корректного закрытия
        // Внутреннее состояние (инкапсулировано)
        private bool _isConnected;
        private DateTime _lastPacketTime = DateTime.MinValue;

        // Событие для ViewModel. Передает готовую структуру.
        public event Action<PacketData>? OnPacketReceived;
        public event Action<string>? OnStatusChanged;
        // Событие, которое сообщает: true - подключено, false - потеряно
        public event Action<bool>? OnConnectionStatusChanged;

        public SerialPacketService()
        {
            // Запускаем фоновый поток мониторинга сразу при создании сервиса
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
                else
                {
                    // Проверка на "зависание" данных (Watchdog)
                    // Если данных нет более 1 секунды при активном подключении
                    if ((DateTime.UtcNow - _lastPacketTime).TotalMilliseconds > 1000)
                    {
                        OnStatusChanged?.Invoke("Данные не поступают. Переподключение...");
                        Disconnect(); // Закрываем порт, чтобы цикл поиска запустился снова
                    }
                }

                // Если не подключились, ждем 2 секунды перед следующей попыткой
                await Task.Delay(2000);
            }
        }

        /// <summary>
        /// Автопоиск устройства по заголовку "UZIS"
        /// </summary>
        private async Task<bool> TryAutoConnectAsync(int baudRate = 921600)
        {
            string[] ports = SerialPortStream.GetPortNames();
            foreach (var port in ports)
            {
                if (_isClosing) return false;

                OnStatusChanged?.Invoke($"Проверка {port}...");
                //await Task.Delay(1500); // возможно поможет избежать BSOD
                SerialPortStream? foundPort = await Task.Run(() => GetPortIfValid(port, baudRate));
                if (foundPort != null)
                {
                    InitializeMainPort(foundPort); // Передаем уже открытый порт
                    return true;
                }
            }
            OnStatusChanged?.Invoke("Устройство не найдено");
            return false;
        }

        private void SetConnectionStatus(bool connected)
        {
            if (_isConnected != connected)
            {
                _isConnected = connected;
                OnConnectionStatusChanged?.Invoke(connected);
            }
        }

        private SerialPortStream? GetPortIfValid(string portName, int baudRate)
        {
            var port = new SerialPortStream(portName, baudRate) { ReadTimeout = 1000, ReadBufferSize = 8192 };

            try
            {
                port.Open();

                // Читаем немного данных для поиска заголовка
                byte[] checkBuf = new byte[2048];
                int read = port.Read(checkBuf, 0, checkBuf.Length);

                if (checkBuf.AsSpan(0, read).IndexOf(Header) != -1)
                {
                    //InitializeMainPort(portName, baudRate);
                    return port;
                }

                port.Dispose();
            }
            catch { try { port.Dispose(); } catch { } }

            return null;
        }

        private void InitializeMainPort(SerialPortStream openedPort)
        {
            _comPort = openedPort;

            // Сбрасываем таймаут чтения, который был нужен для проверки
            _comPort.ReadTimeout = SerialPortStream.InfiniteTimeout;

            // Обработка критических ошибок (отключение кабеля)
            _comPort.ErrorReceived += (s, e) => Disconnect();

            _comPort.DataReceived += (s, e) =>
            {
                // Используем локальную копию ссылки для безопасности
                var port = _comPort;

                if (port == null || !port.IsOpen || _isClosing) return;

                try
                {
                    int toRead = port.BytesToRead;
                    if (toRead == 0) return;

                    byte[] temp = new byte[toRead];
                    int actualRead = port.Read(temp, 0, toRead);

                    lock (_buffer)
                    {
                        _buffer.AddRange(temp.AsSpan(0, actualRead));
                        ParseBuffer();
                    }
                }
                catch (Exception ex)
                {
                    try { port.Dispose(); } catch { }
                    OnStatusChanged?.Invoke($"Ошибка чтения: {ex.Message}");
                }
            };

            SetConnectionStatus(true); // Уведомляем об успехе
            OnStatusChanged?.Invoke($"Подключено: {_comPort.PortName}");
        }

        private void ParseBuffer()
        {
            lock (_buffer)
            {
                // Пока в буфере может быть хотя бы один пакет
                while (_buffer.Count >= TotalPacketSize)
                {
                    var span = CollectionsMarshal.AsSpan(_buffer);
                    int idx = span.IndexOf(Header);

                    // Если заголовка нет вообще
                    if (idx == -1)
                    {
                        // Оставляем последние 3 байта (вдруг заголовок пришел частично)
                        _buffer.RemoveRange(0, _buffer.Count - (Header.Length - 1));
                        break;
                    }

                    // Если перед заголовком мусор — удаляем его
                    if (idx > 0)
                    {
                        _buffer.RemoveRange(0, idx);
                        continue; // Проверяем заново с начала буфера
                    }

                    // Заголовок в начале, проверяем полный ли пакет
                    if (_buffer.Count >= TotalPacketSize)
                    {
                        // Накладываем структуру на память (Zero-Allocation)
                        // Берем "слайс" памяти, ПРОПУСКАЯ первые 4 байта (заголовок)
                        ReadOnlySpan<byte> payloadSpan = span.Slice(Header.Length, PayloadSize);
                        PacketData data = MemoryMarshal.Read<PacketData>(payloadSpan);

                        _lastPacketTime = DateTime.UtcNow; // Обновляем метку времени при каждом пакете
                        // Пробрасываем событие (выполняется в потоке порта!)
                        OnPacketReceived?.Invoke(data);

                        // Удаляем обработанный пакет
                        _buffer.RemoveRange(0, TotalPacketSize);
                    }
                    else
                    {
                        // Ждем докачки байтов
                        break;
                    }
                }
            }
        }

        private void Disconnect()
        {
            // Блокируем создание новых портов во время закрытия
            if (_comPort == null) return;

            try
            {
                _comPort.Dispose(); // Полное освобождение ресурсов
            }
            catch (Exception ex) { }
            finally
            {
                _comPort = null;

                lock (_buffer) { _buffer.Clear(); }
                // Уведомляем о разрыве
                SetConnectionStatus(false);
                OnStatusChanged?.Invoke("Устройство отключено");
            }
        }

        public void StopService()
        {
            _isClosing = true;
            Disconnect();
        }
    }
}
