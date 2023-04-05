// Copyright (c) 2021 Unity Technologies
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// License: https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/blob/develop/com.unity.netcode.gameobjects/LICENSE.md

using System;
using Unity.Networking.Transport;
#if UTP_TRANSPORT_2_0_ABOVE
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
#endif

namespace FishNet.Transporting.FishyUnityTransport.BatchedQueue
{
    /// <summary>Queue for batched messages received through UTP.</summary>
    /// <remarks>This is meant as a companion to <see cref="BatchedSendQueue"/>.</remarks>
    internal class BatchedReceiveQueue
    {
        private byte[] m_Data;
        private int m_Offset;
        private int m_Length;

        public bool IsEmpty => m_Length <= 0;

        /// <summary>
        /// Construct a new receive queue from a <see cref="DataStreamReader"/> returned by
        /// <see cref="NetworkDriver"/> when popping a data event.
        /// </summary>
        /// <param name="reader">The <see cref="DataStreamReader"/> to construct from.</param>
        public BatchedReceiveQueue(DataStreamReader reader)
        {
            m_Data = new byte[reader.Length];
            unsafe
            {
                fixed (byte* dataPtr = m_Data)
                {
#if UTP_TRANSPORT_2_0_ABOVE
                    reader.ReadBytesUnsafe(dataPtr, reader.Length);
#else
                    reader.ReadBytes(dataPtr, reader.Length);
#endif
                }
            }

            m_Offset = 0;
            m_Length = reader.Length;
        }

        /// <summary>
        /// Push the entire data from a <see cref="DataStreamReader"/> (as returned by popping an
        /// event from a <see cref="NetworkDriver">) to the queue.
        /// </summary>
        /// <param name="reader">The <see cref="DataStreamReader"/> to push the data of.</param>
        public void PushReader(DataStreamReader reader)
        {
            // Resize the array and copy the existing data to the beginning if there's not enough
            // room to copy the reader's data at the end of the existing data.
            var available = m_Data.Length - (m_Offset + m_Length);
            if (available < reader.Length)
            {
                if (m_Length > 0)
                {
                    Array.Copy(m_Data, m_Offset, m_Data, 0, m_Length);
                }

                m_Offset = 0;

                while (m_Data.Length - m_Length < reader.Length)
                {
                    Array.Resize(ref m_Data, m_Data.Length * 2);
                }
            }

            unsafe
            {
                fixed (byte* dataPtr = m_Data)
                {
#if UTP_TRANSPORT_2_0_ABOVE
                    reader.ReadBytesUnsafe(dataPtr + m_Offset + m_Length, reader.Length);
#else
                    reader.ReadBytes(dataPtr + m_Offset + m_Length, reader.Length);
#endif
                }
            }

            m_Length += reader.Length;
        }

        /// <summary>Pop the next full message in the queue.</summary>
        /// <returns>The message, or the default value if no more full messages.</returns>
        public ArraySegment<byte> PopMessage()
        {
            if (m_Length < sizeof(int))
            {
                return default;
            }

            var messageLength = BitConverter.ToInt32(m_Data, m_Offset);

            if (m_Length - sizeof(int) < messageLength)
            {
                return default;
            }

            var data = new ArraySegment<byte>(m_Data, m_Offset + sizeof(int), messageLength);

            m_Offset += sizeof(int) + messageLength;
            m_Length -= sizeof(int) + messageLength;

            return data;
        }
    }
}