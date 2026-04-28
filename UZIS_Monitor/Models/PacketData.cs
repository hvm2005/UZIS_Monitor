using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UZIS_Monitor.Models
{
    // Атрибут говорит: эта структура ведет себя как массив из 36 элементов
    [InlineArray(36)]
    public struct EventsBuffer
    {
        private EventData _element0;

        // Добавьте это, чтобы WPF не падал при вызове Equals
        public override bool Equals(object? obj) => false;
        public override int GetHashCode() => 0;
    }

    [ProtoContract]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EventData
    {
        // Поля (занимают память)
        [ProtoMember(1)] private uint TimeRaw;
        [ProtoMember(2)] private short MeanKkmRaw;
        [ProtoMember(3)] private short MeanDinRaw;
        [ProtoMember(4)] private ushort VoltageArcRaw;
        [ProtoMember(5)] private ushort VoltageArc1Raw;
        //[ProtoMember(6)] private ushort NoiseIntRaw;

        // Свойства (не занимают память, вычисляются при обращении)
        public double MeanKkm => MeanKkmRaw * (3.3d / 4.096d);
        public double MeanDin => MeanDinRaw * (3.3d / 4.096d);
        public double VoltageArc => VoltageArcRaw * (3.3d / 4096d) * (940d / 2.80d);
        public double VoltageArc1 => VoltageArcRaw * (3.3d / 4096d) * (940d / 2.80d);
        //public ushort NoiseInt => NoiseIntRaw;
        public double Time => TimeRaw / 30.0d;
        public int M => (int)(MeanKkm * 1000d / Math.Max(VoltageArc - 15.0d, 25.0d));
    }

    [ProtoContract]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketData
    {
        [ProtoMember(1)] private uint NumberRaw;
        [ProtoMember(2)] private uint LineVoltageAccumRaw;
        [ProtoMember(3)] private uint LineCurrentAccumRaw;
        [ProtoMember(4)] private uint LineCounterRaw;
        [ProtoMember(5)] private int SigmaKkmRaw;
        [ProtoMember(6)] private short LinePolarityRaw;
        [ProtoMember(7)] private ushort EvPhase2Raw;
        [ProtoMember(8)] private ushort EvPhase4Raw;
        [ProtoMember(9)] private short AccumulatorRaw;

        // --- Массив вложенных структур ---
        // ВМЕСТО: public EventData[] Events;
        // ТЕПЕРЬ: Полноценный вложенный буфер (не ссылочный тип!)
        private EventsBuffer Events;

        [ProtoMember(10)] private short MthRaw;
        [ProtoMember(11)] private ushort Crc16Raw;

        // Свойство-посредник для Protobuf
        [ProtoMember(12, OverwriteList = true)] public EventData[] ProtoEvents
        {
            get
            {
                // Определяем, сколько элементов реально скопировать (не более 36)
                ushort count = Math.Min(EvPhase2Raw, (ushort)36);

                // Создаем целевой массив
                var arr = new EventData[count];

                // Копируем весь InlineArray в массив за одну операцию (через Span)
                ((ReadOnlySpan<EventData>)Events)[..count].CopyTo(arr);

                return arr;
            }
            set
            {
                if (value != null)
                {
                    // Превращаем входящий массив в Span для эффективного доступа
                    ReadOnlySpan<EventData> source = value;

                    // Получаем Span нашего внутреннего InlineArray
                    Span<EventData> destination = Events;

                    // Определяем, сколько элементов реально скопировать (не более 36)
                    int count = Math.Min(source.Length, 36);

                    // Выполняем быстрое копирование памяти (Slice + CopyTo)
                    source[..count].CopyTo(destination);
                }
            }
        }

        // --- Свойства для DevSavedStats (для UI и логики) ---
        public uint Number => NumberRaw;
        public TimeSpan Time => TimeSpan.FromMilliseconds(NumberRaw * 10);
        public double LineVoltage => LineCounterRaw == 0 ? 0d : Math.Sqrt((double)LineVoltageAccumRaw / (double)LineCounterRaw) * (3.3d / 4096d) * (940d / 2.80d);
        public double LineCurrent => LineCounterRaw == 0 ? 0d : Math.Sqrt((double)LineCurrentAccumRaw / (double)LineCounterRaw) * (3.3d / 4.096d);
        public short Mth => MthRaw;
        public ushort EvPhase2 => EvPhase2Raw;
        public ushort EvPhase4 => EvPhase4Raw;
        public double SigmaKkm => SigmaKkmRaw * (3.3d / 4.096d);
        public string Polarity => LinePolarityRaw == 1 ? "+" : "-";
        public short Accumulator => AccumulatorRaw;
        public bool IsEmpty => EvPhase2Raw + EvPhase4Raw == 0;
        public ushort Crc16 => Crc16Raw;
        public ushort CalcCrc => ComputeCrc16Stm32(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in this), 1))[..^2]);
        public bool IsCrcValid => Crc16 == CalcCrc;

        private static ushort ComputeCrc16Stm32(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF; // Стандартное InitValue для STM32
            const ushort poly = 0x1021;

            foreach (byte b in data)
            {
                // STM32 обрабатывает байты, сдвигая их влево (MSB first)
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ poly);
                    else
                        crc <<= 1;
                }
            }
            return crc;
        }
    }
}
