using System;

using ACE.Server.Network.GameMessages;

using log4net;

namespace ACE.Server.Network
{
    internal class MessageFragment
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly ILog packetLog = LogManager.GetLogger(System.Reflection.Assembly.GetEntryAssembly(), "Packets");

        public GameMessage Message { get; private set; }

        public uint Sequence { get; set; }

        public ushort Index { get; set; }

        public ushort Count { get; set; }

        public int DataLength => (int)Message.Data.Length;

        public int DataRemaining { get; private set; }

        public int NextSize
        {
            get
            {
                var dataSize = DataRemaining;
                if (dataSize > PacketFragment.MaxFragmentDataSize)
                    dataSize = PacketFragment.MaxFragmentDataSize;
                return PacketFragmentHeader.HeaderSize + dataSize;
            }
        }

        public int TailSize => PacketFragmentHeader.HeaderSize + (DataLength % PacketFragment.MaxFragmentDataSize);

        public bool TailSent { get; private set; }

        public MessageFragment(GameMessage message, uint sequence)
        {
            Message = message;
            DataRemaining = DataLength;
            Sequence = sequence;
            Count = (ushort)(Math.Ceiling((double)DataLength / PacketFragment.MaxFragmentDataSize));
            Index = 0;
            if (Count == 1)
                TailSent = true;
            packetLog.DebugFormat("Sequence {0}, count {1}, DataRemaining {2}", sequence, Count, DataRemaining);
        }

        public ServerPacketFragment GetTailFragment()
        {
            var index = (ushort)(Count - 1);
            TailSent = true;
            return CreateServerFragment(index);
        }

        public ServerPacketFragment GetNextFragment()
        {
            return CreateServerFragment(Index++);
        }

        private ServerPacketFragment CreateServerFragment(ushort index)
        {
            packetLog.DebugFormat("Creating ServerFragment for index {0}", index);
            if (index >= Count)
                throw new ArgumentOutOfRangeException("index", index, "Passed index is greater then computed count");

            var position = index * PacketFragment.MaxFragmentDataSize;
            if (position > DataLength)
                throw new ArgumentOutOfRangeException("index", index, "Passed index computes to invalid position size");

            if (DataRemaining <= 0)
                throw new InvalidOperationException("There is no data remaining");

            var dataToSend = DataLength - position;
            if (dataToSend > PacketFragment.MaxFragmentDataSize)
                dataToSend = PacketFragment.MaxFragmentDataSize;

            if (DataRemaining < dataToSend)
                throw new InvalidOperationException("More data to send then data remaining!");

            // Read data starting at position reading dataToSend bytes.
            //
            // This copies straight out of the backing array rather than doing Seek + Read, because
            // ONE GameMessage is routinely shared by many sessions: EnqueueBroadcast hands the same
            // message object to every recipient, and so do the CreateObject / DeleteObject broadcast
            // fan-outs. NetworkManager ticks sessions outbound through a Parallel.ForEach, so two
            // sessions can be fragmenting the same message at the same moment. Seek + Read mutates
            // the shared stream's Position between the two calls, so an interleaving lets the second
            // Read start where the first one finished - silently producing a fragment full of the
            // wrong bytes rather than throwing. A position-free copy cannot interleave: nothing here
            // writes to the message or its stream.
            //
            // GetBuffer() is legal for every GameMessage. Data has a private setter and is only ever
            // assigned in GameMessage's two constructors, as new MemoryStream() or
            // new MemoryStream(int capacity) - neither wraps a caller-supplied array, so both leave
            // the buffer publicly visible. The bounds checks above guarantee
            // position + dataToSend <= DataLength <= buffer length.
            byte[] data = new byte[dataToSend];
            Buffer.BlockCopy(Message.Data.GetBuffer(), position, data, 0, dataToSend);

            // Build ServerPacketFragment structure
            ServerPacketFragment fragment = new ServerPacketFragment(data);
            fragment.Header.Sequence = Sequence;
            fragment.Header.Id = 0x80000000;
            fragment.Header.Count = Count;
            fragment.Header.Index = index;
            fragment.Header.Queue = (ushort)Message.Group;

            DataRemaining -= dataToSend;
            packetLog.DebugFormat("Done creating ServerFragment for index {0}. After reading {1} DataRemaining {2}", index, dataToSend, DataRemaining);
            return fragment;
        }
    }
}
