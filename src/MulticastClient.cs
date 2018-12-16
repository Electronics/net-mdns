﻿using Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Makaretu.Dns
{
    class MulticastClient : IDisposable
    {
        static readonly ILog log = LogManager.GetLogger(typeof(MulticastClient));

        const int MulticastPort = 5353;
        static readonly IPAddress MulticastAddressIp4 = IPAddress.Parse("224.0.0.251");
        static readonly IPAddress MulticastAddressIp6 = IPAddress.Parse("FF02::FB");
        static readonly IPEndPoint MdnsEndpointIp6 = new IPEndPoint(MulticastAddressIp6, MulticastPort);
        static readonly IPEndPoint MdnsEndpointIp4 = new IPEndPoint(MulticastAddressIp4, MulticastPort);

        readonly IPAddress multicastLoopbackAddress;
        readonly List<UdpClient> receivers;
        readonly ConcurrentDictionary<IPAddress, UdpClient> senders = new ConcurrentDictionary<IPAddress, UdpClient>();

        public event EventHandler<UdpReceiveResult> MessageReceived;

        public MulticastClient(bool useIPv4, bool useIpv6, IEnumerable<NetworkInterface> nics)
        {
            // Setup the receivers.
            receivers = new List<UdpClient>();

            UdpClient receiver4 = null;
            if (useIPv4)
            {
                receiver4 = new UdpClient(AddressFamily.InterNetwork);
                receiver4.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver4.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
                receivers.Add(receiver4);
            }

            UdpClient receiver6 = null;
            if (useIpv6)
            {
                receiver6 = new UdpClient(AddressFamily.InterNetworkV6);
                receiver6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));
                receiver6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(MulticastAddressIp6));
                receivers.Add(receiver6);
            }

            // Get the IP addresses that we should send to.
            var addreses = nics
                .SelectMany(GetNetworkInterfaceLocalAddresses)
                .Where(a => (useIPv4 && a.AddressFamily == AddressFamily.InterNetwork)
                    || (useIpv6 && a.AddressFamily == AddressFamily.InterNetworkV6));
            foreach (var address in addreses)
            {
                if (senders.Keys.Contains(address))
                {
                    continue;
                }

                var localEndpoint = new IPEndPoint(address, MulticastPort);
                log.Debug($"Will send to {localEndpoint}");
                var sender = new UdpClient(address.AddressFamily);
                try
                {
                    switch (address.AddressFamily)
                    {
                        case AddressFamily.InterNetwork:
                            receiver4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddressIp4, address));
                            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            sender.Client.Bind(localEndpoint);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddressIp4));
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                            break;
                        case AddressFamily.InterNetworkV6:
                            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            sender.Client.Bind(localEndpoint);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(MulticastAddressIp6));
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
                            break;
                        default:
                            throw new NotSupportedException($"Address family {address.AddressFamily}.");
                    }

                    // Assigning multicastLoopbackAddress to first avalable address that we use for sending messages
                    if (senders.TryAdd(address, sender) && multicastLoopbackAddress == null)
                    {
                        multicastLoopbackAddress = address;
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
                {
                    // VPN NetworkInterfaces
                    sender.Dispose();
                }
                catch (Exception)
                {
                    sender.Dispose();
                    throw;
                }
            }

            // Start listening for messages.
            foreach (var r in receivers)
            {
                Listen(r);
            }
        }

        public async Task SendAsync(byte[] message)
        {
            await Task.WhenAll(senders.Select(x => x.Value.SendAsync(message, message.Length, x.Key.AddressFamily == AddressFamily.InterNetwork ? MdnsEndpointIp4 : MdnsEndpointIp6))).ConfigureAwait(false);
        }

        void Listen(UdpClient receiver)
        {
            Task.Run(async () =>
            {
                try
                {
                    var task = receiver.ReceiveAsync();

                    _ = task.ContinueWith(x => Listen(receiver), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.RunContinuationsAsynchronously);

                    _ = task.ContinueWith(x => FilterMulticastLoopbackMessages(x.Result), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.RunContinuationsAsynchronously);

                    await task.ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            });
        }

        /// <summary>
        /// For multi NICs we accepting MulticastLoopback message only from one of available addresses, used for sending messages
        /// </summary>
        /// <param name="result">Received message <see cref="UdpReceiveResult"/></param>
        void FilterMulticastLoopbackMessages(UdpReceiveResult result)
        {
            log.Debug($"got datagram on {result.RemoteEndPoint}");

            var remoteIP = result.RemoteEndPoint.Address;

            if (senders.ContainsKey(remoteIP) && !remoteIP.Equals(multicastLoopbackAddress))
            {
                log.Debug("ignored");
                return;
            }

            MessageReceived?.Invoke(this, result);
        }

        IEnumerable<IPAddress> GetNetworkInterfaceLocalAddresses(NetworkInterface nic)
        {
            return nic
                .GetIPProperties()
                .UnicastAddresses
                .Select(x => x.Address)
                .Where(x => x.AddressFamily != AddressFamily.InterNetworkV6 || x.IsIPv6LinkLocal)
                ;
        }

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    foreach (var receiver in receivers)
                    {
                        receiver.Dispose();
                    }

                    foreach (var address in senders.Keys)
                    {
                        if (senders.TryRemove(address, out var sender))
                        {
                            sender.Dispose();
                        }
                    }
                }

                disposedValue = true;
            }
        }

        ~MulticastClient()
        {
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
