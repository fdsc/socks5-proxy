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

namespace vinnysocks5proxy
{
    public partial class ListenConfiguration: IDisposable, IComparable<ListenConfiguration>
    {
        public partial class Connection: IDisposable
        {
            protected readonly byte[] ErrorAuthMethodResponse = new byte[] { 0x05, 0xFF };
            protected readonly byte[] NoAuthMethodResponse    = new byte[] { 0x05, 0x00 };
            protected readonly byte[] PwdAuthMethodResponse   = new byte[] { 0x05, 0x02 };

            public const int EC_success                       = 0x00;
            public const int EC_general_SOCKS_server_failure  = 0x01;
            public const int EC_Denied                        = 0x02;
            public const int EC_Network_unreachable           = 0x03;
            public const int EC_Host_unreachable              = 0x04;
            public const int EC_Connection_refused            = 0x05;
            public const int EC_Command_not_supported         = 0x07;
            public const int EC_Address_type_not_supported    = 0x08;
            

            // Установка нового соединения клиента с прокси
            public Connection(Socket connection, ListenConfiguration listen)
            {
                this.connection = connection;
                this.listen     = listen;

                ThreadPool.QueueUserWorkItem
                (
                    delegate
                    {
                        bool successNegotiation = false; 
                        try
                        {
                            var b  = new byte[4096];
                            var bb = new BytesBuilder();
    
                            // Установление соединения
                            // Ожидаем байты коннекта по socks5 https://datatracker.ietf.org/doc/html/rfc1928
                            var available = waitAvailableBytes(3, timeout: int.MaxValue);
                            
                            if (available == 0)
                                return;
    
                            if (available > b.Length)
                            {
                                available = b.Length;
                            }
    
                            connection.Receive(b, available, SocketFlags.None);
                            // listen.Log(BitConverter.ToString(b, 0, available));
                            
                            
                            // Версия протокола socks - 0x05
                            // Второй байт - это количество поддерживаемых методов (по байту на метод)
                            // Таким образом, всего сейчас должно быть получено не менее чем methodsCount + 2 байта
                            var methodsCount = b[1];
                            if (b[0] != 0x05 || methodsCount == 0 || methodsCount != available - 2)
                            {
                                processInvalidProtocolMessage();
                                return;
                            }
    
                            // 0 - без аутентификации; 2 - парольная аутентификация
                            int method = listen.users.Count == 0 ? 0 : 2;
                            for (var i = 2; i < available; i++)
                            {
                                if (b[i] == method)
                                    goto findMethod;
                            }
    
                            // Обработка ошибки
                            // Посылаем сообщение о том, что ни один из методов не подходит
                            connection.Send(ErrorAuthMethodResponse);
    
                            lock (listen.log)
                            {
                                listen.LogErrorForConnection($"The correct authorization method was not found (check user and password in client and in server)", connection, b, available);
                            }

                            return;
    
                            findMethod:
    
                            if (method == 0)
                                connection.Send(NoAuthMethodResponse);
                            else
                                connection.Send(PwdAuthMethodResponse);
    
                            available = waitAvailableBytes(7);
                            if (available == 0)
                                return;
    
                            if (available > b.Length)
                            {
                                available = b.Length;
                            }
    
                            connection.Receive(b, available, SocketFlags.None);
                            // listen.Log(BitConverter.ToString(b, 0, available));
                            
                            // Снова проверяем версию протокола
                            if (b[0] != 0x05)
                            {
                                processInvalidProtocolMessage();
                                return;
                            }
    
                            // Второй байт - команда соединения
                            // CONNECT '01'
                            // BIND '02'
                            // UDP ASSOCIATE '03'
                            // Мы принимаем только CONNECT
                            if (b[1] != 0x01)
                            {
                                listen.LogErrorForConnection($"vinny-socks5-proxy can accept only 'CONNECT' command, but command " + b[1], connection, b, available);
                                processResponseForRequest(bb, EC_Command_not_supported);
                                return;
                            }
                            
                            // Читаем тип адреса ATYP
                            // Проверяем, что он корректен и разрешён в конфигах
                            var addressType = b[3];
                            if (addressType != 0x01 && addressType != 0x03 && addressType != 0x04)
                            {
                                listen.LogErrorForConnection($"The unknown address type: " + addressType, connection, b, available);
                                processResponseForRequest(bb, EC_Address_type_not_supported);
                                return;
                            }
                            
                            if (addressType == 0x01 && !listen.namesGranted_ipv4)
                            {
                                listen.LogErrorForConnection($"ipv4 address is denied by configuration", connection, b, available);
                                processResponseForRequest(bb, EC_Address_type_not_supported);
                                return;
                            }
    
                            if (addressType == 0x03 && !listen.namesGranted_domain)
                            {
                                listen.LogErrorForConnection($"The domain name type of address is denied by configuration", connection, b, available);
                                processResponseForRequest(bb, EC_Address_type_not_supported);
                                return;
                            }
    
                            if (addressType == 0x04 && !listen.namesGranted_ipv6)
                            {
                                processResponseForRequest(bb, EC_Address_type_not_supported);
                                listen.LogErrorForConnection($"ipv6 address is denied by configuration", connection, b, available);
                                processResponseForRequest(bb, EC_Address_type_not_supported);
                                return;
                            }
                            
                            var ConnectToPort = b[available - 1] + (b[available - 2] << 8);

                            try
                            {
    
                                // Смотрим, какой именно адрес и готовим для него почву
                                bool connected = false;
                                int  networkUnreachable = 0;
                                int  connectionRefused  = 0;
                                int  anotherError       = 0;
                                if (addressType == 0x01 || addressType == 0x04) // Это IP-адреса
                                {
                                    bb.addWithCopy(b, -1, 4, available - 2);
                                    var ConnectToIP = new IPAddress(bb.getBytes());
                                    connectionTo    = new Socket(ConnectToIP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                                    var ipe         = new IPEndPoint(ConnectToIP, ConnectToPort);
                                    
                                    try
                                    {
                                        listen.LogForConnection("Request for connection to " + ipe, connection);
                                        connectionTo.Connect(ipe);
                                        connected = true;
                                    }
                                    catch (SocketException e)
                                    {
                                        listen.LogForConnection("Error with try " + ipe + "\r\n" + e.Message, connection);
                                        return;
                                    }
                                }
                                else // Это доменные имена
                                {
                                    // b[4] - это размер доменного имени
                                    if (7 + b[4] != available)
                                    {
                                        processInvalidProtocolMessage();
                                        return;
                                    }
    
                                    bb.addWithCopy(b, -1, 5, 5 + b[4]);
                                    var domainName = Encoding.ASCII.GetString(bb.getBytes());
                                    listen.LogForConnection("Request for connection to '" + domainName + "'", connection);
                                    
    
                                    var addresses = Dns.GetHostAddresses(domainName);
    
                                    // Перебираем возможные адреса соединения, если с одним не удалось соединить
                                    foreach (var addr in addresses)
                                    {
                                        connectionTo    = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                                        var ipe         = new IPEndPoint(addr, ConnectToPort);
                                        
                                        listen.LogForConnection("Try connection to " + ipe, connection);
                                        try
                                        {
                                            connectionTo.Connect(ipe);
                                            connected = true;
                                            break;
                                        }
                                        catch (SocketException e)
                                        {
                                            if (e.ErrorCode == 10061)
                                                connectionRefused++;
                                            else
                                            if (e.ErrorCode == 10051)
                                                networkUnreachable++;
                                            else
                                                anotherError++;

                                            listen.LogForConnection("Error with try " + ipe + "\r\n" + e.Message, connection);
                                        }
                                    }
                                }
    
                                bb.Clear();
    
                                if (!connected)
                                {
                                    if (connectionRefused > 0)
                                        processResponseForRequest(bb, EC_Connection_refused);
                                    else
                                    if (anotherError > 0)
                                        processResponseForRequest(bb, EC_Host_unreachable);
                                    else
                                    if (networkUnreachable > 0)
                                        processResponseForRequest(bb, EC_Network_unreachable);
                                    else
                                        processResponseForRequest(bb, EC_general_SOCKS_server_failure);

                                    return;
                                }

                                // Здесь мы используем исключительно адрес, нам переданный
                                // Хотя по стандарту должны передавать адрес, с которым установлено соединение
                                processResponseForRequest(bb, EC_success);

                                doProcessTraffic();
                                successNegotiation = true;
                            }
                            catch (SocketException e)
                            {
                                if (e.ErrorCode == 11001)
                                {
                                    listen.LogErrorForConnection($"Could not resolve host", connection, null, 0);
                                    processResponseForRequest(bb, EC_Network_unreachable);
                                }
                                else
                                {
                                    listen.LogErrorForConnection($"Exception " + e.Message + "\r\n\r\n" + e.StackTrace, connection, b, available);
                                    processResponseForRequest(bb, EC_general_SOCKS_server_failure);
                                }
                            }
                            catch (Exception e)
                            {
                                listen.LogErrorForConnection($"Exception " + e.Message + "\r\n\r\n" + e.StackTrace, connection, b, available);
                                processResponseForRequest(bb, EC_general_SOCKS_server_failure);
                            }
                            
                        }
                        catch (Exception e)
                        {
                            lock (listen.log)
                            {
                                listen.LogErrorForConnection($"Exception " + e.Message + "\r\n\r\n" + e.StackTrace, connection, null, 0);
                            }
                        }
                        finally
                        {
                            if (!successNegotiation)
                            {
                                connection.Shutdown(SocketShutdown.Both);
                                this.Dispose();
                            }
                        }
	                }
                );
            }
            
            public void processInvalidProtocolMessage()
            {
                lock (listen.log)
                {
                    listen.Log($"error for connection {connection.RemoteEndPoint.ToString()}");
                    listen.Log($"Invalid protocol. Socks5 protocol must be setted. Check protocol record in the client");
                }

                this.Dispose();
            }
            
            public void processResponseForRequest(BytesBuilder bb, byte replyCode)
            {
                bb.addByte(0x05);           // Версия протокола
                bb.addByte(replyCode);      // Код ошибки
                /*
                    REP    Reply field:
                     o  X'00' succeeded
                     o  X'01' general SOCKS server failure
                     o  X'02' connection not allowed by ruleset
                     o  X'03' Network unreachable
                     o  X'04' Host unreachable
                     o  X'05' Connection refused
                     o  X'06' TTL expired
                     o  X'07' Command not supported
                     o  X'08' Address type not supported
                     o  X'09' to X'FF' unassigned
                 */
                bb.addByte(0x00);
                if (connection.LocalEndPoint.AddressFamily == AddressFamily.InterNetwork)
                {
                    bb.addByte(0x01);   // ATYP = ipv4
                    var ipe = connectionTo.LocalEndPoint as IPEndPoint;
                    bb.add(ipe.Address.GetAddressBytes());
                    bb.addUshort((ushort) ipe.Port);
                }
                else
                {
                    bb.addByte(0x04);   // ATYP = ipv6
                    var ipe = connectionTo.LocalEndPoint as IPEndPoint;
                    bb.add(ipe.Address.GetAddressBytes());
                    //bb.addUshort((ushort) ipe.Port);
                    bb.addByte((byte) (ipe.Port >> 8));
                    bb.addByte((byte) ipe.Port);
                }

                var response = bb.getBytes();
                connection.Send(response, 0, response.Length, SocketFlags.None);
            }
            
            public void Pulse()
            {
                lock (connection)
                    Monitor.PulseAll(connection);

                lock (connectionTo)
                    Monitor.PulseAll(connectionTo);
            }
        }

        public void newConnection(Socket connection)
        {
            lock (connections)
                this.connections.Add(new Connection(connection, this));

            Log("new connection from " + connection.RemoteEndPoint.ToString());
        }
    }
}
