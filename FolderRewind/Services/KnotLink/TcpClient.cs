/*
 * KnotLink SDK - C#
 * Copyright (c) 2024-2026 KnotLink Contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    public enum KnotLinkFrameFormat
    {
        /// <summary>The four-byte big-endian length prefix used by KnotLinkService 2.0.0.</summary>
        LengthPrefixed,

        /// <summary>The SDK-main framing variant that prepends KK 00 02 before the length.</summary>
        MagicV2
    }

    public sealed class KlTcpClient : IAsyncDisposable, IDisposable
    {
        private const string HeartbeatMessage = "heartbeat";
        private const string HeartbeatResponse = "heartbeat_response";
        private const int MaxMessageSize = 16 * 1024 * 1024; // 16MB
        private static readonly byte[] Magic = new byte[] { 0x4B, 0x4B, 0x00, 0x02 }; // KK + 版本号 2.0
        private const int MagicLen = 4;
        private const int LengthFieldSize = 4;

        private readonly TimeSpan _heartbeatInterval;
        private readonly KnotLinkFrameFormat _frameFormat;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private Task? _readTask;
        private Task? _heartbeatTask;
        private readonly byte[] _lenBuffer = new byte[4];
        private readonly byte[] _magicBuffer = new byte[4];
        private readonly MemoryStream _recvBuffer = new();
        private int _disposed;

        public Func<string, Task>? OnDataReceivedAsync { get; set; }
        public Func<Exception, Task>? OnErrorAsync { get; set; }
        public bool Running => _client?.Connected == true && !_cts.IsCancellationRequested;

        public KlTcpClient(
            TimeSpan? heartbeatInterval = null,
            KnotLinkFrameFormat frameFormat = KnotLinkFrameFormat.LengthPrefixed)
        {
            _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromMinutes(3);
            _frameFormat = frameFormat;
        }

        // ---------- 连接 ----------
        public async Task<bool> ConnectAsync(string host, int port)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(KlTcpClient));

            _client = new TcpClient();
            await _client.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _client.GetStream();

            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
            return true;
        }

        // ---------- 发送（加长度前缀） ----------
        public async Task SendAsync(string data, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(KlTcpClient));
            if (_stream == null) throw new InvalidOperationException("Not connected.");

            byte[] payload = Encoding.UTF8.GetBytes(data);
            if (payload.Length > MaxMessageSize)
                throw new ArgumentException($"Message too large: {payload.Length} > {MaxMessageSize}");

            // 4 字节大端长度头
            byte[] lenBytes = BitConverter.GetBytes(payload.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(KlTcpClient));

                if (_frameFormat == KnotLinkFrameFormat.MagicV2)
                {
                    await _stream.WriteAsync(Magic, 0, MagicLen, cancellationToken).ConfigureAwait(false);
                }

                await _stream.WriteAsync(lenBytes, 0, LengthFieldSize, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ---------- 接收循环（缓冲 + 解析长度前缀） ----------
        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) return;

            byte[] chunk = new byte[4096];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0) break; // 对端关闭

                    lock (_recvBuffer)
                    {
                        _recvBuffer.Write(chunk, 0, bytesRead);
                    }

                    // 尽可能多地解析出完整消息
                    while (TryExtractMessage(out byte[]? msgBytes))
                    {
                        if (msgBytes == null) break;
                        string text = Encoding.UTF8.GetString(msgBytes);
                        if (text == HeartbeatResponse)
                            continue; // 忽略心跳回复

                        if (OnDataReceivedAsync != null)
                        {
                            try
                            {
                                await OnDataReceivedAsync(text).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // 用户回调异常不应终止底层接收循环，但需要对调用方可见。
                                await ReportErrorAsync(ex).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常退出 */ }
            catch (Exception ex) when (Volatile.Read(ref _disposed) == 0)
            {
                await ReportErrorAsync(ex).ConfigureAwait(false);
            }
            catch (Exception) { /* 释放时关闭 socket 可能触发异常，静默退出 */ }
            finally
            {
                // 连接断开，取消所有任务
                _cts.Cancel();
            }
        }

        private async Task ReportErrorAsync(Exception ex)
        {
            if (OnErrorAsync != null)
            {
                try
                {
                    await OnErrorAsync(ex).ConfigureAwait(false);
                    return;
                }
                catch
                {
                    // 错误处理器自身失败时退回到标准错误输出。
                }
            }

            Console.Error.WriteLine(ex.Message);
        }

        // ---------- 从缓冲区提取一条完整消息 ----------
        private bool TryExtractMessage(out byte[]? message)
        {
            message = null;
            int msgLenToThrow = 0;
            bool shouldThrow = false;

            lock (_recvBuffer)
            {
                long bufferLen = _recvBuffer.Length;
                int headerLen = _frameFormat == KnotLinkFrameFormat.MagicV2
                    ? MagicLen + LengthFieldSize
                    : LengthFieldSize;
                if (bufferLen < headerLen) return false;

                _recvBuffer.Position = 0;
                if (_frameFormat == KnotLinkFrameFormat.MagicV2)
                {
                    _recvBuffer.Read(_magicBuffer, 0, MagicLen);
                    if (!_magicBuffer.AsSpan().SequenceEqual(Magic))
                    {
                        _recvBuffer.SetLength(0);
                        _recvBuffer.Position = 0;
                        throw new InvalidDataException("Magic mismatch, disconnecting");
                    }
                }

                // 读取长度（大端）
                _recvBuffer.Read(_lenBuffer, 0, LengthFieldSize);
                int msgLen = BitConverter.ToInt32(_lenBuffer, 0);
                if (BitConverter.IsLittleEndian) msgLen = System.Net.IPAddress.NetworkToHostOrder(msgLen);

                if (msgLen <= 0 || msgLen > MaxMessageSize)
                {
                    _recvBuffer.SetLength(0);
                    _recvBuffer.Position = 0;
                    shouldThrow = true;
                    msgLenToThrow = msgLen;
                    // 退出 lock 块再抛异常
                }
                else if (bufferLen < headerLen + msgLen)
                {
                    return false; // 消息体未完整
                }
                else
                {
                    byte[] msg = new byte[msgLen];
                    _recvBuffer.Position = headerLen;
                    _recvBuffer.Read(msg, 0, msgLen);

                    byte[] remaining = new byte[bufferLen - headerLen - msgLen];
                    _recvBuffer.Position = headerLen + msgLen;
                    _recvBuffer.Read(remaining, 0, remaining.Length);
                    _recvBuffer.SetLength(0);
                    _recvBuffer.Position = 0;
                    _recvBuffer.Write(remaining, 0, remaining.Length);

                    message = msg;
                    return true;
                }
            }

            if (shouldThrow)
                throw new InvalidDataException($"Invalid message length: {msgLenToThrow}");
            return false;
        }

        // ---------- 心跳循环 ----------
        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await SendAsync(HeartbeatMessage, cancellationToken).ConfigureAwait(false);
                    }
                    catch { /* 忽略心跳发送失败 */ }
                }
            }
            catch (OperationCanceledException) { /* 正常 */ }
        }

        private void CloseSocket()
        {
            _stream?.Dispose();
            _client?.Close();
        }

        // ---------- 释放 ----------
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _cts.Cancel();
            bool sendLockTaken = false;
            try
            {
                sendLockTaken = _sendLock.Wait(TimeSpan.FromSeconds(2));
                CloseSocket();
            }
            finally
            {
                if (sendLockTaken) _sendLock.Release();
            }

            try { _readTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _heartbeatTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

            _sendLock.Dispose();
            _cts.Dispose();
            _recvBuffer.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _cts.Cancel();
            bool sendLockTaken = false;
            try
            {
                sendLockTaken = await _sendLock.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                CloseSocket();
            }
            finally
            {
                if (sendLockTaken) _sendLock.Release();
            }

            if (_readTask != null) try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            if (_heartbeatTask != null) try { await _heartbeatTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }

            _sendLock.Dispose();
            _cts.Dispose();
            await _recvBuffer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
