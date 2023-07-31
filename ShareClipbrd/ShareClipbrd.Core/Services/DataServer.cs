﻿using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using GuardNet;
using ShareClipbrd.Core.Clipboard;
using ShareClipbrd.Core.Configuration;
using ShareClipbrd.Core.Extensions;
using ShareClipbrd.Core.Helpers;

namespace ShareClipbrd.Core.Services {
    public interface IDataServer {
        void Start();
        Task Stop();
    }

    public class DataServer : IDataServer {
        readonly ISystemConfiguration systemConfiguration;
        readonly IDialogService dialogService;
        readonly IDispatchService dispatchService;
        readonly IProgressService progressService;
        readonly IConnectStatusService connectStatusService;
        CancellationTokenSource? cts;
        TaskCompletionSource<bool> tcsStopped;

        public DataServer(
            ISystemConfiguration systemConfiguration,
            IDialogService dialogService,
            IDispatchService dispatchService,
            IProgressService progressService,
            IConnectStatusService connectStatusService
            ) {
            Guard.NotNull(systemConfiguration, nameof(systemConfiguration));
            Guard.NotNull(dialogService, nameof(dialogService));
            Guard.NotNull(dispatchService, nameof(dispatchService));
            Guard.NotNull(progressService, nameof(progressService));
            Guard.NotNull(connectStatusService, nameof(connectStatusService));
            this.systemConfiguration = systemConfiguration;
            this.dialogService = dialogService;
            this.dispatchService = dispatchService;
            this.progressService = progressService;
            this.connectStatusService = connectStatusService;

            tcsStopped = new TaskCompletionSource<bool>(true);
            tcsStopped.TrySetResult(true);
        }

        static async ValueTask<MemoryStream> HandleData(NetworkStream stream, int dataSize, CancellationToken cancellationToken) {
            var memoryStream = new MemoryStream(dataSize);
            byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(CommunProtocol.ChunkSize);
            try {
                while(memoryStream.Length < dataSize) {
                    int receivedBytes = await stream.ReadAsync(receiveBuffer, cancellationToken);
                    if(receivedBytes == 0) {
                        break;
                    }
                    await memoryStream.WriteAsync(new ReadOnlyMemory<byte>(receiveBuffer, 0, receivedBytes), cancellationToken);
                }
            } finally {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
            }

            await stream.WriteAsync(CommunProtocol.SuccessData, cancellationToken);
            return memoryStream;
        }

        static string RecreateTempDirectory() {
            const string path = "ShareClipbrd_60D54950";
            var tempDir = Path.Combine(Path.GetTempPath(), path);
            if(Directory.Exists(tempDir)) {
                try {
                    Directory.Delete(tempDir, true);
                } catch { }
            }
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        static async ValueTask<string?> ReceiveFormat(NetworkStream stream, CancellationToken cancellationToken) {
            var format = await stream.ReadUTF8StringAsync(cancellationToken);
            if(string.IsNullOrEmpty(format)) {
                return null;
            }
            await stream.WriteAsync(CommunProtocol.SuccessFormat, cancellationToken);
            return format;
        }

        static async ValueTask<Int64> ReceiveSize(NetworkStream stream, CancellationToken cancellationToken) {
            var size = await stream.ReadInt64Async(cancellationToken);
            await stream.WriteAsync(CommunProtocol.SuccessSize, cancellationToken);
            return size;
        }

        async ValueTask HandleClient(TcpClient tcpClient, CancellationToken cancellationToken) {
            var clipboardData = new ClipboardData();

            connectStatusService.Online();
            var sessionDir = new Lazy<string>(RecreateTempDirectory);

            while(!cancellationToken.IsCancellationRequested) {
                var stream = tcpClient.GetStream();

                if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.Version) {
                    await stream.WriteAsync(CommunProtocol.Error, cancellationToken);
                    throw new NotSupportedException("Wrong version of the other side");
                }
                await stream.WriteAsync(CommunProtocol.SuccessVersion, cancellationToken);

                await using(progressService.Begin(ProgressMode.Receive)) {
                    try {
                        long total;
                        try {
                            total = await ReceiveSize(stream, cancellationToken);
                        } catch(EndOfStreamException) {
                            return;
                        }
                        var format = await ReceiveFormat(stream, cancellationToken);

                        if(format == ClipboardFile.Format.FileDrop) {
                            var fileReceiver = new FileReceiver(progressService, stream, sessionDir.Value, total, cancellationToken);
                            await fileReceiver.Receive();
                            var receivedFiles = DirectoryHelper.GetDirectoriesAndFiles(sessionDir.Value);
                            dispatchService.ReceiveFiles(receivedFiles);

                        } else if(format == ClipboardData.Format.WaveAudio) {

                        } else {
                            progressService.SetMaxTick(total);
                            while(!string.IsNullOrEmpty(format) && !cancellationToken.IsCancellationRequested) {
                                var size = await ReceiveSize(stream, cancellationToken);
                                progressService.Tick(size);
                                clipboardData.Add(format, await HandleData(stream, (int)size, cancellationToken));

                                if(await stream.ReadUInt16Async(cancellationToken) != CommunProtocol.MoreData) {
                                    break;
                                }

                                format = await ReceiveFormat(stream, cancellationToken);
                            }
                            dispatchService.ReceiveData(clipboardData);
                        }

                        Debug.WriteLine($"tcpServer success finished");

                    } catch(OperationCanceledException ex) {
                        Debug.WriteLine($"tcpServer canceled {ex}");
                    } catch(Exception ex) {
                        await dialogService.ShowError(ex);
                    }
                }
            }
        }

        public void Start() {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            tcsStopped = new TaskCompletionSource<bool>();
            var cancellationToken = cts.Token;
            Task.Run(async () => {

                while(!cancellationToken.IsCancellationRequested) {
                    try {
                        var adr = NetworkHelper.ResolveHostName(systemConfiguration.HostAddress);
                        var tcpServer = new TcpListener(adr.Address, adr.Port);
                        try {
                            Debug.WriteLine($"start tcpServer: {adr}");
                            tcpServer.Start();

                            while(!cancellationToken.IsCancellationRequested) {
                                using var tcpClient = await tcpServer.AcceptTcpClientAsync(cancellationToken);
                                Debug.WriteLine($"tcpServer accept  {tcpClient.Client.RemoteEndPoint}");

                                await HandleClient(tcpClient, cancellationToken);
                            }
                        } catch(OperationCanceledException ex) {
                            Debug.WriteLine($"tcpServer canceled {ex}");
                        } catch(Exception ex) {
                            await dialogService.ShowError(ex);
                        }

                        Debug.WriteLine($"tcpServer stop");
                        tcpServer.Stop();
                        Debug.WriteLine($"tcpServer stopped");

                    } catch(SocketException ex) {
                        await dialogService.ShowError(ex);
                    } catch(ArgumentException ex) {
                        await dialogService.ShowError(ex);
                    }
                }
                connectStatusService.Offline();
                tcsStopped.TrySetResult(true);
            }, cancellationToken);
        }

        public Task Stop() {
            Debug.WriteLine($"tcpServer request to stop");
            cts?.Cancel();
            if(tcsStopped.Task.IsCompleted) {
                connectStatusService.Offline();
            }
            return tcsStopped.Task;
        }
    }
}
