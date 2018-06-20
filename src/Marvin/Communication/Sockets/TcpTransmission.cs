﻿using System;
using System.IO;
using System.Net.Sockets;
using Marvin.Container;
using Marvin.Logging;
using Marvin.Serialization;

namespace Marvin.Communication.Sockets
{
    /// <summary>
    ///
    /// </summary>
    internal class TcpTransmission : IBinaryTransmission, IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly IMessageInterpreter _interpreter;
        private readonly TcpClient _client;

        /// <summary>
        /// Logger for logging - makes sense ;)
        /// </summary>
        private readonly IModuleLogger _logger;

        /// <summary>
        /// Delegate invoked when connection was lost
        /// </summary>
        public event EventHandler Disconnected;

        /// <summary>
        /// Callback to forward transmission exceptions to connection
        /// </summary>
        public event EventHandler<Exception> ExceptionOccured;

        /// <summary>
        /// Initialize TcpTransmission
        /// </summary>
        /// <param name="client">The client.</param>
        /// <param name="interpreter">The interpreter.</param>
        /// <param name="logger">Logger used to write exceptions to log</param>
        public TcpTransmission(TcpClient client, IMessageInterpreter interpreter, IModuleLogger logger)
        {
            _interpreter = interpreter;
            _client = client;
            _stream = client.GetStream();
            _logger = logger;
        }

        private void RaiseException(Exception ex)
        {
            if (ExceptionOccured == null)
                _logger.LogException(LogLevel.Error, ex, "TcpTransmission encountered an error");
            else
                ExceptionOccured(this, ex);
        }

        private void RaiseDisconnected()
        {
            if (Disconnected == null)
                _logger.LogEntry(LogLevel.Warning, "Client disconnected, but listener already removed!");
            else
                Disconnected(this, new EventArgs());
        }

        /// <summary>
        /// Trigger to check if we are still connected
        /// http://stackoverflow.com/a/4667582/6082960
        /// http://tldp.org/HOWTO/TCP-Keepalive-HOWTO/overview.html
        /// </summary>
        public void ConfigureKeepAlive(int interval, int timeout)
        {
            // Create config array
            var index = 0;
            var socketConfig = new byte[12]; // 3 * 4 byte
            InlineConverter.Include(1, socketConfig, ref index);
            InlineConverter.Include(interval, socketConfig, ref index);
            InlineConverter.Include(timeout, socketConfig, ref index);

            // Configure socket
            var socket = _client.Client;
            socket.IOControl(IOControlCode.KeepAliveValues, socketConfig, null);
        }

        #region Send

        /// <summary>
        /// Sends the specified message.
        /// </summary>
        /// <param name="message">The message.</param>
        public void Send(BinaryMessage message)
        {
            var bytes = _interpreter.SerializeMessage(message);
            _stream.BeginWrite(bytes, 0, bytes.Length, SendComplete, null);
        }

        /// <summary>
        /// Callback. Is called when the sending was completed.
        /// </summary>
        /// <param name="result">The result.</param>
        private void SendComplete(IAsyncResult result)
        {
            try
            {
                _stream.EndWrite(result);
            }
            catch (Exception ex)
            {
                Disconnect(ex);
            }
        }

        #endregion

        #region Receive

        /// <summary>
        /// Start reading shit
        /// </summary>
        public void StartReading()
        {
            var context = _interpreter.CreateContext();
            BeginRead(context);
        }

        private void BeginRead(IReadContext transmission)
        {
            try
            {
                if (_disconnected)
                    return;

                _stream.BeginRead(transmission.ReadBuffer, transmission.CurrentIndex, transmission.ReadSize, ReadComplete, transmission);
            }
            catch (ObjectDisposedException)
            {
                Disconnect();
            }
            catch (IOException)
            {
                Disconnect();
            }
        }

        private void ReadComplete(IAsyncResult ar)
        {
            int read;

            if (_disconnected)
                return;

            try
            {
                read = _stream.EndRead(ar);
            }
            catch (Exception ex)
            {
                Disconnect(ex);
                return;
            }

            var transmission = (IReadContext)ar.AsyncState;
            ByteReadResult result;

            if (read > 0)
            {
                result = _interpreter.ProcessReadBytes(transmission, read, PublishMessage);
            }
            else
            {
                Disconnect();
                return;
            }

            // Error in stream - send last will and close transmission
            if (result == ByteReadResult.Failure)
            {
                byte[] lastWill;
                if (_interpreter.ErrorResponse(transmission, out lastWill))
                    _stream.Write(lastWill, 0, lastWill.Length);

                Disconnect(new InvalidHeaderException("Header invalid or no matching validator found!"));
                return;
            }

            BeginRead(transmission);
        }

        private void PublishMessage(BinaryMessage message)
        {
            if (Received == null && _disconnected) // Still feels like a hack
                _logger.LogEntry(LogLevel.Error, "Connection already closed, but a final message was received and can not be published!");
            else
                Received.Invoke(this, message);
        }

        ///
        public event EventHandler<BinaryMessage> Received;

        #endregion

        private bool _disconnected;

        private void Disconnect(Exception ex)
        {
            RaiseException(ex);
            Disconnect();
        }

        internal void Disconnect()
        {
            lock (this)
            {
                if (_disconnected)
                    return;
                _disconnected = true;
            }

            _stream.Dispose();
            _client.Close();
            RaiseDisconnected();
        }

        ///
        public void Dispose()
        {
            Disconnect();
        }
    }
}