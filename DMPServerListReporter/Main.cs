using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using DarkMultiPlayerServer;
using MessageStream2;

namespace DMPServerListReporter
{
    public class Main : DMPPlugin
    {
        //Bump number when we change the format
        private const int HEARTBEAT_ID = 0;
        private const int REPORTING_PROTOCOL_ID = 2;
        //Heartbeat every 10 seconds
        private const int CONNECTION_HEARTBEAT = 10000;
        //Connection retry attempt every 120s
        private const int CONNECTION_RETRY = 60000;
        private double lastMessageSendTime = double.NegativeInfinity;
        //Settings file name
        private const string TOKEN_FILE = "ReportingToken.txt";
        private const string DESCRIPTION_FILE = "ReportingDescription.txt";
        private const string OLD_SETTINGS_FILE = "ReportingSettings.txt";
        private const string NEW_SETTINGS_FILE = "ReportingSettings.xml";
        //Plus, we can explicitly terminate the thread to kill the connection upon shutdown.
        private Thread reportThread;
        TcpClient currentConnection;
        private AutoResetEvent sendEvent = new AutoResetEvent(false);
        private Queue<byte[]> sendMessages = new Queue<byte[]>();
        //Settings
        ReportingSettings settingsStore = new ReportingSettings();
        //Client state tracking
        List<string> connectedPlayers = new List<string>();

        public Main()
        {
            settingsStore.LoadSettings(OLD_SETTINGS_FILE, NEW_SETTINGS_FILE, TOKEN_FILE, DESCRIPTION_FILE);
            CommandHandler.RegisterCommand("reloadreporter", ReloadSettings, "Reload the reporting plugin settings");
        }

        private void ReloadSettings(string commandText)
        {
            StopReporter();
            settingsStore = new ReportingSettings();
            settingsStore.LoadSettings(OLD_SETTINGS_FILE, NEW_SETTINGS_FILE, TOKEN_FILE, DESCRIPTION_FILE);
            StartReporter();
        }

        public static string CalculateSHA256Hash(string text)
        {
            UTF8Encoding encoder = new UTF8Encoding();
            byte[] encodedBytes = encoder.GetBytes(text);
            StringBuilder sb = new StringBuilder();
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] fileHashData = sha.ComputeHash(encodedBytes);
                //Byte[] to string conversion adapted from MSDN...
                for (int i = 0; i < fileHashData.Length; i++)
                {
                    sb.Append(fileHashData[i].ToString("x2"));
                }
            }
            return sb.ToString();
        }

        public override void OnServerStart()
        {
            StartReporter();
        }

        /*
        public override void OnUpdate()
        {
            if (!connectedStatus && loadedSettings && Server.serverRunning && (Server.serverClock.ElapsedMilliseconds > (lastConnectionTry + CONNECTION_RETRY)))
            {
                lastConnectionTry = Server.serverClock.ElapsedMilliseconds;
                connectThread = new Thread(new ThreadStart(ConnectToServer));
                connectThread.Start();
            }
        }
        */

        public override void OnClientAuthenticated(ClientObject client)
        {
            connectedPlayers.Add(client.playerName);
            ReportData();
        }

        public override void OnClientDisconnect(ClientObject client)
        {
            connectedPlayers.Remove(client.playerName);
            ReportData();
        }

        public override void OnServerStop()
        {
            StopReporter();
        }

        private void StartReporter()
        {
            reportThread = new Thread(ReporterMain);
            reportThread.IsBackground = true;
            reportThread.Start();
        }

        private void ReporterMain()
        {
            while (true)
            {
                TcpClient newConnection = ConnectToServer();
                if (newConnection != null)
                {
                    currentConnection = newConnection;
                    try
                    {
                        SendReportMain();
                    }
                    catch (Exception e)
                    {
                        if (!(e is ThreadAbortException))
                        {
                            currentConnection = null;
                            DarkLog.Debug("Disconnected reporter, exception: " + e.Message);
                            DarkLog.Debug("Reconnecting in 5 seconds...");
                            Thread.Sleep(5000);
                        }
                    }
                }
                else
                {
                    DarkLog.Debug("All reporters are down, trying again in 60 seconds");
                    Thread.Sleep(60000);
                }
            }
        }

        private void StopReporter()
        {
            DarkLog.Debug("Stopping reporter");
            reportThread.Abort();
            try
            {
                currentConnection.Close();
            }
            catch
            {
                //Don't care.
            }
        }

        private TcpClient ConnectToServer()
        {
            foreach (string currentEndpoint in settingsStore.reportingEndpoint)
            {
                try
                {
                    string addressString;
                    string portString;
                    if (currentEndpoint.Contains("[") || currentEndpoint.Contains("]:"))
                    {
                        int startIndex = currentEndpoint.IndexOf("[") + 1;
                        int endIndex = currentEndpoint.LastIndexOf("]");
                        addressString = currentEndpoint.Substring(startIndex, endIndex - startIndex);
                        portString = currentEndpoint.Substring(endIndex + 2);
                    }
                    else
                    {
                        int portIndex = currentEndpoint.LastIndexOf(":");
                        addressString = currentEndpoint.Substring(0, portIndex);
                        portString = currentEndpoint.Substring(portIndex + 1);
                    }
                    int port = Int32.Parse(portString);
                    IPAddress[] connectAddresses = new IPAddress[1];
                    if (!IPAddress.TryParse(addressString, out connectAddresses[0]))
                    {
                        connectAddresses = Dns.GetHostAddresses(addressString);
                    }
                    foreach (IPAddress testAddress in connectAddresses)
                    {
                        TcpClient newConnection = new TcpClient(testAddress.AddressFamily);
                        IAsyncResult ar = newConnection.BeginConnect(testAddress, port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(5000))
                        {
                            if (newConnection.Connected)
                            {
                                //Connected!
                                newConnection.EndConnect(ar);
                                currentConnection = newConnection;
                                DarkLog.Debug("Connected to " + currentEndpoint + " (" + testAddress + ")");
                                return newConnection;
                            }
                            else
                            {
                                //Failed to connect - try next one
                                DarkLog.Debug("Failed to connect to " + currentEndpoint + " (" + testAddress + ")");
                            }
                        }
                        else
                        {
                            //Failed to connect - try next one
                            DarkLog.Debug("Failed to connect to " + currentEndpoint + " (" + testAddress + ")");
                        }
                    }
                }
                catch (Exception e)
                {
                    DarkLog.Debug("Error connecting to " + currentEndpoint + ", exception: " + e.Message);
                }
            }
            return null;
        }

        private void SendReportMain()
        {
            sendMessages.Clear();
            ReportData();
            while (true)
            {
                sendEvent.WaitOne(100);
                while (true)
                {
                    byte[] sendBytes = null;
                    lock (sendMessages)
                    {
                        if (sendMessages.Count > 0)
                        {
                            sendBytes = sendMessages.Dequeue();
                        }
                    }
                    if (sendBytes != null)
                    {
                        SendNetworkMessage(sendBytes);
                    }
                    else
                    {
                        break;
                    }
                }
                if (Server.serverClock.ElapsedMilliseconds > (lastMessageSendTime + CONNECTION_HEARTBEAT))
                {
                    lastMessageSendTime = Server.serverClock.ElapsedMilliseconds;
                    //Queue a heartbeat to prevent the connection from timing out
                    QueueHeartbeat();
                }
            }
        }

        private void SendNetworkMessage(byte[] data)
        {
            currentConnection.GetStream().Write(data, 0, data.Length);
        }

        private void ReportData()
        {
            string[] currentPlayers = GetServerPlayerArray();
            if (currentPlayers.Length == 1)
            {
                DarkLog.Debug("Sending report: 1 player.");
            }
            else
            {
                DarkLog.Debug("Sending report: " + currentPlayers.Length + " players.");
            }
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<string>(settingsStore.serverHash);
                mw.Write<string>(Settings.settingsStore.serverName);
                mw.Write<string>(settingsStore.description);
                mw.Write<int>(Settings.settingsStore.port);
                mw.Write<string>(settingsStore.gameAddress);
                //Constants are baked, so we have to reflect them.
                int protocolVersion = (int)typeof(DarkMultiPlayerCommon.Common).GetField("PROTOCOL_VERSION").GetValue(null);
                mw.Write<int>(protocolVersion);
                string programVersion = (string)typeof(DarkMultiPlayerCommon.Common).GetField("PROGRAM_VERSION").GetValue(null);
                mw.Write<string>(programVersion);
                mw.Write<int>(Settings.settingsStore.maxPlayers);
                mw.Write<int>((int)Settings.settingsStore.modControl);
                mw.Write<string>(Server.GetModControlSHA());
                mw.Write<int>((int)Settings.settingsStore.gameMode);
                mw.Write<bool>(Settings.settingsStore.cheats);
                mw.Write<int>((int)Settings.settingsStore.warpMode);
                mw.Write<long>(Server.GetUniverseSize());
                mw.Write<string>(settingsStore.banner);
                mw.Write<string>(settingsStore.homepage);
                mw.Write<int>(Settings.settingsStore.httpPort);
                mw.Write<string>(settingsStore.admin);
                mw.Write<string>(settingsStore.team);
                mw.Write<string>(settingsStore.location);
                mw.Write<bool>(settingsStore.fixedIP);
                mw.Write<string[]>(currentPlayers);
                QueueNetworkMessage(mw.GetMessageBytes());
            }
        }

        private string[] GetServerPlayerArray()
        {
            return connectedPlayers.ToArray();
        }

        private void QueueHeartbeat()
        {
            byte[] messageBytes = new byte[8];
            BitConverter.GetBytes(HEARTBEAT_ID).CopyTo(messageBytes, 0);
            BitConverter.GetBytes(0).CopyTo(messageBytes, 4);
            lock (sendMessages)
            {
                sendMessages.Enqueue(messageBytes);
            }
            sendEvent.Set();
        }

        private void QueueNetworkMessage(byte[] data)
        {
            //Prefix the TCP message frame and append the payload.
            byte[] messageBytes = new byte[data.Length + 8];
            BitConverter.GetBytes(REPORTING_PROTOCOL_ID).CopyTo(messageBytes, 0);
            BitConverter.GetBytes(data.Length).CopyTo(messageBytes, 4);
            Array.Copy(data, 0, messageBytes, 8, data.Length);
            lock (sendMessages)
            {
                sendMessages.Enqueue(messageBytes);
            }
            sendEvent.Set();
        }
    }
}

