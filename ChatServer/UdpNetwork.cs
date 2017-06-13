﻿//    ExampleChatApplication - Example Binary Network Application
//    Copyright (C) 2017 James Forshaw
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.
//
//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with this program.  If not, see <http://www.gnu.org/licenses/>.

using ChatProtocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ChatServer
{
    sealed class UdpNetworkListener : INetworkListener
    {
        UdpClient _client;
        Dictionary<IPEndPoint, UdpClientEntry> _clients;
        CancellationTokenSource _cancel_source;

        public UdpNetworkListener(int port, bool global)
        {
            _client = new UdpClient(new IPEndPoint(global ? IPAddress.Any : IPAddress.Loopback, port));
            _cancel_source = new CancellationTokenSource();
            _clients = new Dictionary<IPEndPoint, UdpClientEntry>();
        }

        public async Task<AcceptState> AcceptConnection()
        {
            while (true)
            {
                UdpReceiveResult result = await _client.ReceiveAsync();
                lock (_clients)
                {
                    if (_clients.ContainsKey(result.RemoteEndPoint))
                    {
                        _clients[result.RemoteEndPoint].Enqueue(result.Buffer);
                    }
                    else
                    {
                        UdpClientEntry client = new UdpClientEntry(this, result.RemoteEndPoint, _cancel_source.Token);
                        client.Enqueue(result.Buffer);
                        _clients.Add(result.RemoteEndPoint, client);
                        return new AcceptState(client, result.RemoteEndPoint.ToString(), "UDP", this, null);
                    }
                }
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
            _cancel_source.Cancel();
        }

        sealed class UdpClientEntry : IClientEntry
        {
            private UdpNetworkListener _listener;
            private IPEndPoint _endpoint;
            private Queue<byte[]> _queue;
            private SemaphoreSlim _semaphore;
            private CancellationToken _token;

            public string UserName { get; set; }
            public string HostName { get; set; }
            public IPEndPoint ClientEndpoint { get; set; }

            internal void Enqueue(byte[] data)
            {
                lock (_queue)
                {
                    _queue.Enqueue(data);
                    _semaphore.Release();
                }
            }

            public bool WritePacket(ProtocolPacket packet)
            {
                try
                {
                    MemoryStream stm = new MemoryStream();
                    BinaryNetworkTransport.WritePacket(packet, new BinaryWriter(stm), false);
                    byte[] data = stm.ToArray();
                    return _listener._client.SendAsync(data, data.Length, _endpoint).GetAwaiter().GetResult() == data.Length;
                }
                catch
                {
                    return false;
                }
            }

            public async Task<ReadPacketState> ReadPacketAsync()
            {
                try
                {
                    await _semaphore.WaitAsync(_token);
                    byte[] data;
                    lock (_queue)
                    {
                        data = _queue.Dequeue();
                    }

                    MemoryStream stm = new MemoryStream(data);
                    return new ReadPacketState(this, 
                        BinaryNetworkTransport.ReadPacket(new BinaryReader(stm), data.Length), null);
                }
                catch (Exception ex)
                {
                    return new ReadPacketState(this, null, ex);
                }
            }

            public void SetXorKey(byte xorkey)
            {
                // Do nothing.
            }

            public void Dispose()
            {
                lock (_listener._clients)
                {
                    _listener._clients.Remove(_endpoint);
                }
            }

            public UdpClientEntry(UdpNetworkListener listener, IPEndPoint endpoint, CancellationToken token)
            {
                _listener = listener;
                _endpoint = endpoint;
                _semaphore = new SemaphoreSlim(0);
                _queue = new Queue<byte[]>();
                _token = token;
                UserName = String.Format("User_{0}", endpoint);
                HostName = endpoint.ToString();
            }
        }
    }
}