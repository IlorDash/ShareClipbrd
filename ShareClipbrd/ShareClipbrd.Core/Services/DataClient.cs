﻿using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using Clipboard.Core;
using GuardNet;
using ShareClipbrd.Core.Clipboard;
using ShareClipbrd.Core.Configuration;
using ShareClipbrd.Core.Extensions;
using ShareClipbrd.Core.Helpers;


namespace ShareClipbrd.Core.Services {
    public interface IDataClient {
        Task SendFileDropList(StringCollection files);
        Task SendData(ClipboardData clipboardData);
        void Start();
        void Stop();
    }

    public class DataClient : IDataClient {
        readonly ISystemConfiguration systemConfiguration;
        readonly IProgressService progressService;
        readonly IConnectStatusService connectStatusService;
        readonly IDialogService dialogService;
        readonly System.Timers.Timer pingTimer;
        readonly ITimeService timeService;
        readonly IAddressDiscoveryService addressDiscoveryService;
        TcpClient client;
        CancellationTokenSource cts;

        public DataClient(
            ISystemConfiguration systemConfiguration,
            IProgressService progressService,
            IConnectStatusService connectStatusService,
            IDialogService dialogService,
            ITimeService timeService,
            IAddressDiscoveryService addressDiscoveryService
            ) {
            Guard.NotNull(systemConfiguration, nameof(systemConfiguration));
            Guard.NotNull(progressService, nameof(progressService));
            Guard.NotNull(connectStatusService, nameof(connectStatusService));
            Guard.NotNull(dialogService, nameof(dialogService));
            Guard.NotNull(timeService, nameof(timeService));
            Guard.NotNull(addressDiscoveryService, nameof(addressDiscoveryService));
            this.systemConfiguration = systemConfiguration;
            this.progressService = progressService;
            this.connectStatusService = connectStatusService;
            this.dialogService = dialogService;
            this.timeService = timeService;
            this.addressDiscoveryService = addressDiscoveryService;

            client = new();
            cts = new();
            pingTimer = new();
            pingTimer.AutoReset = false;
            pingTimer.Elapsed += OnPingTimerEvent;
        }

        async ValueTask<NetworkStream> Handshake() {
            var cancellationToken = cts.Token;
            var stream = client.GetStream();
            await stream.WriteAsync(CommunProtocol.Version, cancellationToken);
            if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.SuccessVersion) {
                await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                throw new NotSupportedException("Wrong version of the other side");
            }
            connectStatusService.ClientOnline();
            return stream;
        }

        public async Task SendFileDropList(StringCollection fileDropList) {
            cts.Cancel();
            var inProcess = !pingTimer.Enabled;
            if(inProcess) {
                Debug.WriteLine("--- inProcess 0");
                await Task.Delay(1000);
            }
            cts = new();
            var cancellationToken = cts.Token;
            try {
                await Connect();
                var stream = await Handshake();
                var fileTransmitter = new FileTransmitter(progressService, stream);
                await fileTransmitter.Send(fileDropList, cancellationToken);
            } catch(SocketException ex) {
                await dialogService.ShowError(ex);
            } catch(IOException ex) {
                await dialogService.ShowError(ex);
            } catch(ArgumentException ex) {
                await dialogService.ShowError(ex);
            } catch(OperationCanceledException) {
                client.Close();
            } finally {
                pingTimer.Enabled = !cancellationToken.IsCancellationRequested;
            }
        }

        static async Task SendFormat(string format, NetworkStream stream, CancellationToken cancellationToken) {
            await stream.WriteAsync(format, cancellationToken);
            if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.SuccessFormat) {
                await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                throw new NotSupportedException($"Others do not support clipboard format: {format}");
            }
        }

        static async Task SendSize(Int64 size, NetworkStream stream, CancellationToken cancellationToken) {
            await stream.WriteAsync(size, cancellationToken);
            if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.SuccessSize) {
                await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                throw new NotSupportedException($"Others do not support size: {size}");
            }
        }

        public async Task SendData(ClipboardData clipboardData) {
            cts.Cancel();
            var inProcess = !pingTimer.Enabled;
            if(inProcess) {
                Debug.WriteLine("--- inProcess 0");
                await Task.Delay(1000);
            }
            cts = new();
            var cancellationToken = cts.Token;
            try {
                await Connect();
                await using(progressService.Begin(ProgressMode.Send)) {
                    var totalLenght = clipboardData.GetTotalLenght();
                    progressService.SetMaxTick(totalLenght);
                    var stream = await Handshake();


                    await stream.WriteAsync(totalLenght, cancellationToken);
                    if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.SuccessSize) {
                        await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                        throw new NotSupportedException($"Others do not support total: {totalLenght}");
                    }

                    for(var i = 0; i < clipboardData.Formats.Count; i++) {
                        var clipboard = clipboardData.Formats[i];
                        progressService.Tick(clipboard.Stream.Length);
                        await SendFormat(clipboard.Format, stream, cancellationToken);
                        await SendSize(clipboard.Stream.Length, stream, cancellationToken);
                        clipboard.Stream.Position = 0;

                        await clipboard.Stream.CopyToAsync(stream, cancellationToken);

                        if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.SuccessData) {
                            await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                            throw new NotSupportedException($"Transfer data error");
                        }

                        var moreData = i < clipboardData.Formats.Count - 1;
                        if(moreData) {
                            await stream.WriteAsync(CommunProtocol.MoreData, cancellationToken);
                        }
                    }
                    await stream.WriteAsync(CommunProtocol.Finish, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            } catch(SocketException ex) {
                await dialogService.ShowError(ex);
            } catch(IOException ex) {
                Debug.WriteLine($"tcpClient IO exception {ex}");
            } catch(ArgumentException ex) {
                await dialogService.ShowError(ex);
            } catch(InvalidOperationException ex) {
                await dialogService.ShowError(ex);
            } catch(OperationCanceledException) {
                client.Close();
            } finally {
                pingTimer.Enabled = !cancellationToken.IsCancellationRequested;
            }
        }

        async Task Connect() {
            pingTimer.Enabled = false;
            var connected = IsSocketConnected(client.Client);
            if(!connected) {
                connectStatusService.ClientOffline();
                client.Close();
                client = new();

                IPEndPoint ipEndPoint;
                if(AddressResolver.UseAddressDiscoveryService(systemConfiguration.PartnerAddress, out string id, out int? mandatoryPort)) {
                    if(mandatoryPort.HasValue) {
                        throw new ArgumentException("mdns port for the partner address is not needed");
                    }
                    ipEndPoint = await addressDiscoveryService.Discover(id);
                } else {
                    ipEndPoint = NetworkHelper.ResolveHostName(systemConfiguration.PartnerAddress);
                }

                await client.ConnectAsync(ipEndPoint.Address, ipEndPoint.Port, cts.Token);
            }
        }

        static bool IsSocketConnected(Socket s) {
            if(s == null) {
                return false;
            }
            try {
                return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);
            } catch(ObjectDisposedException) {
                return false;
            }
        }

        async Task Ping() {
            var cancellationToken = cts.Token;
            try {
                await Connect();
                if(!IsSocketConnected(client.Client)) {
                    return;
                }
                var stream = await Handshake();

                await stream.WriteAsync((Int64)0, cancellationToken);
                await stream.ReadUInt16Async(cancellationToken);
            } catch(ArgumentException ex) {
                await dialogService.ShowError(ex);
            } catch(Exception) {
            }
            pingTimer.Enabled = !cancellationToken.IsCancellationRequested;
            if(pingTimer.Interval != timeService.DataClientPingPeriod.TotalMilliseconds) {
                pingTimer.Interval = timeService.DataClientPingPeriod.TotalMilliseconds;
            }
        }

        void OnPingTimerEvent(object? source, ElapsedEventArgs e) {
            _ = Ping();
        }

        public void Start() {
            pingTimer.Enabled = true;
        }

        public void Stop() {
            cts.Cancel();
            pingTimer.Enabled = false;
        }
    }
}
