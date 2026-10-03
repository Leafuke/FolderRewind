/*
 * KnotLink SDK - C#
 * Copyright (c) 2024-2026 KnotLink Contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>
    /// 用于向 KnotLink 服务发送查询请求并接收回复。
    /// 支持同步等待和异步发送，并可设置回调处理异步回复。
    /// </summary>
    public sealed class OpenSocketQuerier : IDisposable
    {
        private readonly KlTcpClient _client;
        private readonly ManualResetEventSlim _queryEvent = new(false);
        private readonly object _lock = new();

        private string _appId;
        private string _openSocketId;
        private readonly string _host;
        private readonly int _port;
        private Action<string>? _recvCallback;
        private string? _queryResult;
        private bool _isQuerying;

        /// <summary>
        /// 初始化 Querier 并连接到 KnotLink 服务。
        /// </summary>
        /// <param name="appId">应用标识</param>
        /// <param name="openSocketId">开放 Socket ID</param>
        /// <param name="host">服务器地址，默认 127.0.0.1</param>
        /// <param name="port">端口，默认 6376</param>
        public OpenSocketQuerier(string appId, string openSocketId, string host = "127.0.0.1", int port = 6376)
        {
            _appId = appId;
            _openSocketId = openSocketId;
            _host = host;
            _port = port;

            _client = new KlTcpClient();
            _client.OnDataReceivedAsync = OnDataReceivedAsync;
        }

        public Task InitializeAsync()
        {
            return _client.ConnectAsync(_host, _port);
        }

        /// <summary>
        /// 更新应用 ID 和 Socket ID。
        /// </summary>
        public void SetConfig(string appId, string openSocketId)
        {
            lock (_lock)
            {
                _appId = appId;
                _openSocketId = openSocketId;
            }
        }

        /// <summary>
        /// 设置接收数据的回调函数（用于异步模式）。
        /// </summary>
        /// <param name="callback">接收字符串数据的回调</param>
        public void SetRecvFunc(Action<string> callback) => _recvCallback = callback;

        /// <summary>
        /// 同步发送查询并等待回复（阻塞当前线程）。
        /// </summary>
        /// <param name="question">问题内容</param>
        /// <param name="timeoutMs">超时时间（毫秒），默认 5000</param>
        /// <returns>服务器回复的字符串</returns>
        /// <exception cref="TimeoutException">超时未收到回复</exception>
        /// <exception cref="InvalidOperationException">已有查询在进行中</exception>
        public string Query(string question, int timeoutMs = -1)
        {
            // 锁定并标记查询开始
            lock (_lock)
            {
                if (_isQuerying)
                    throw new InvalidOperationException("A query is already in progress.");
                _isQuerying = true;
                _queryResult = null;
                _queryEvent.Reset();
            }

            try
            {
                SendQueryAsync(question).GetAwaiter().GetResult();

                // 等待回复或超时
                if (!_queryEvent.Wait(timeoutMs < 0 ? System.Threading.Timeout.Infinite : timeoutMs))
                {
                    lock (_lock) _isQuerying = false;
                    throw new TimeoutException($"Query timed out after {timeoutMs}ms.");
                }

                lock (_lock)
                {
                    _isQuerying = false;
                    return _queryResult ?? throw new InvalidOperationException("No result received.");
                }
            }
            catch
            {
                // 发生异常时确保重置状态
                lock (_lock) _isQuerying = false;
                throw;
            }
        }

        /// <summary>
        /// 异步发送查询（即发即忘），不等待回复。
        /// 回复将由 SetRecvFunc 设置的回调处理。
        /// </summary>
        /// <param name="question">问题内容</param>
        public void QueryAsync(string question)
        {
            _ = SendQueryAsync(question);
        }

        /// <summary>
        /// 发送查询数据到服务器（内部方法）。
        /// </summary>
        private async Task SendQueryAsync(string question)
        {
            string packet;
            lock (_lock)
            {
                packet = $"{_appId}-{_openSocketId}&*&{question}";
            }
            await _client.SendAsync(packet).ConfigureAwait(false);
        }

        /// <summary>
        /// 处理接收到的数据（由 KlTcpClient 回调）。
        /// 如果是同步查询等待，则设置结果并释放事件；否则调用用户回调。
        /// </summary>
        private async Task OnDataReceivedAsync(string data)
        {
            bool isQuerying;
            lock (_lock) { isQuerying = _isQuerying; }

            if (isQuerying)
            {
                lock (_lock)
                {
                    _queryResult = data;
                    _isQuerying = false;  // 收到后不再等待
                    _queryEvent.Set();
                }
            }
            else
            {
                // 异步模式，调用用户回调
                _recvCallback?.Invoke(data);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 释放资源。
        /// </summary>
        public void Dispose()
        {
            _client?.Dispose();
            _queryEvent?.Dispose();
        }
    }
}
