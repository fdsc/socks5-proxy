﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static vinnysocks5proxy.Helper;
using cryptoprime;
using System.Text;
using System.Diagnostics;

namespace vinnysocks5proxy
{
    public partial class ListenConfiguration: IDisposable, IComparable<ListenConfiguration>
    {
        public partial class Connection: IDisposable
        {
            public Socket connection   = null;
            public Socket connectionTo = null;
            public long   SizeOfTransferredDataTo   = 0;
            public long   SizeOfTransferredDataFrom = 0;
            public readonly ListenConfiguration listen = null;

            public int waitAvailableBytes(int minBytes = 1, int timeout = 100, int countOfTimeouts = 100)
            {
                int available = 0;
                lock (connection)
                {
                        available = connection.Available;
                    int count     = 0;
                    while (available < minBytes)
                    {
                        Monitor.Wait(connection, timeout);
    
                        available = connection.Available;
    
                        if (count > countOfTimeouts)
                        {
                            listen.Log($"error for connection {connection.RemoteEndPoint.ToString()}: not enought bytes; available {available}", 0);
                            // this.Dispose();
                            return 0;
                        }
                    }
                }

                return available;
            }

            public void Dispose()
            {
                Dispose(false);
            }

            public bool doTerminate = false;
            public void Dispose(bool doNotDelete)
            {
                doTerminate = true;
                start.Stop();
                LogForConnection($"Connection closed; sended bytes {SizeOfTransferredDataTo.ToString("N0")}, received bytes {SizeOfTransferredDataFrom.ToString("N0")}; time {start.Elapsed}", connection, 2);

                if (!doNotDelete)
                lock (listen.connections)
                    listen.connections.Remove(this);

                try { connection  ?.Dispose(); } catch {}
                try { connectionTo?.Dispose(); } catch {}
                connection   = null;
                connectionTo = null;
            }
        }
    }
}