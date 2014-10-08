using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DarkMultiPlayerServer;
using MessageStream;

namespace DMPServerListReporter
{
    public class Main : DMPPlugin
    {
        //Bump number when we change the format
        private const int HEARTBEAT_ID = 0;
        private const int REPORTING_PROTOCOL_ID = 1;
        //Heartbeat every 10 seconds
        private const int CONNECTION_HEARTBEAT = 10000;
        //Connection retry attempt every 120s
        private const int CONNECTION_RETRY = 120000;
        //Settings file name
        private const string SETTINGS_FILE = "ReportingServer.txt";
        //Last time the connection was attempted
        private long lastConnectionTry = long.MinValue;
        //Last message send time
        private long lastMessageSendTime = long.MinValue;
        //Whether we are connected to the reporting server
        private bool connectedStatus;
        //Actual TCP connection
        private TcpClient connection;
        //Uses explicit threads - The async methods whack out for some people in some rare cases it seems.
        //Plus, we can explicitly terminate the thread to kill the connection upon shutdown.
        private Thread connectThread;
        private Thread sendThread;
        private AutoResetEvent sendEvent = new AutoResetEvent(false);
        private Queue<byte[]> sendMessages = new Queue<byte[]>();
        //Reporting endpoint
        private IPEndPoint reportingEndpoint;

        public Main()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            string settingsFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), SETTINGS_FILE);
            if (!File.Exists(settingsFileFullPath))
            {
                File.WriteAllText(settingsFileFullPath, "dmp.52k.de:9001");
            }
            bool reloadSettings = false;
            string connectionAddress = File.ReadAllLines(settingsFileFullPath)[0];
            try
            {
                string[] connectionParts = connectionAddress.Split(':');
                string address = connectionParts[0];
                string port = connectionParts[1];
                IPAddress reportingIP;
                int reportingPort;
                //Make sure the port is correct
                if (Int32.TryParse(port, out reportingPort))
                {
                    if (reportingPort > 0 && reportingPort < 65535)
                    {
                        //Try parsing the address directly before trying a DNS lookup
                        if (!IPAddress.TryParse(address, out reportingIP))
                        {
                            IPHostEntry entry = Dns.GetHostEntry(address);
                            reportingIP = entry.AddressList[0];
                        }
                        reportingEndpoint = new IPEndPoint(reportingIP, reportingPort);
                    }
                    else
                    {
                        reloadSettings = true;
                    }

                }
                else
                {
                    reloadSettings = true;
                }
            }
            catch
            {
                reloadSettings = true;
            }
            if (reloadSettings)
            {
                //Load with the default settings if anything is incorrect.
                File.Delete(settingsFileFullPath);
                LoadSettings();
            }
        }

        public override void OnUpdate()
        {
            if (!connectedStatus && (Server.serverClock.ElapsedMilliseconds > (lastConnectionTry + CONNECTION_RETRY)))
            {
                lastConnectionTry = Server.serverClock.ElapsedMilliseconds;
                connectThread = new Thread(new ThreadStart(ConnectToServer));
                connectThread.Start();
            }
        }

        public override void OnClientAuthenticated(ClientObject client)
        {
            ReportData();
        }

        public override void OnClientDisconnect(ClientObject client)
        {
            ReportData();
        }

        private void ConnectToServer()
        {
            try
            {
                connection = new TcpClient();
                connection.Connect(reportingEndpoint);
                if (connection.Connected)
                {
                    DarkLog.Debug("Connected to reporting server");
                    sendMessages.Clear();
                    ReportData();
                    connectedStatus = true;
                    sendThread = new Thread(new ThreadStart(SendThreadMain));
                    sendThread.Start();
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Error connecting to reporting server: " + e);
            }
        }

        private void SendThreadMain()
        {
            try
            {
                while (true)
                {
                    lock (sendMessages)
                    {
                        if (Server.serverClock.ElapsedMilliseconds > (lastMessageSendTime + CONNECTION_HEARTBEAT))
                        {
                            lastMessageSendTime = Server.serverClock.ElapsedMilliseconds;
                            //Queue a heartbeat to prevent the connection from timing out
                            QueueHeartbeat();
                        }
                        if (sendMessages.Count == 0)
                        {
                            sendEvent.WaitOne(100);
                        }
                        else
                        {
                            while (sendMessages.Count > 0)
                            {
                                SendNetworkMessage(sendMessages.Dequeue());
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Reporting send error: " + e);
                connectedStatus = false;
            }
        }

        private void SendNetworkMessage(byte[] data)
        {
            connection.GetStream().Write(data, 0, data.Length);
        }

        private void ReportData()
        {
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<bool>(Settings.settingsStore.cheats);
                mw.Write<int>((int)Settings.settingsStore.gameMode);
                mw.Write<int>((int)Settings.settingsStore.modControl);
                mw.Write<int>((int)Settings.settingsStore.warpMode);
                mw.Write<int>(Settings.settingsStore.maxPlayers);
                mw.Write<int>(Server.playerCount);
                mw.Write<string>(Server.players);
                mw.Write<long>(Server.lastPlayerActivity);
                mw.Write<int>(Settings.settingsStore.port);
                mw.Write<int>(Settings.settingsStore.httpPort);
                mw.Write<int>(DarkMultiPlayerCommon.Common.PROTOCOL_VERSION);
                mw.Write<string>(DarkMultiPlayerCommon.Common.PROGRAM_VERSION);
                mw.Write<string>(Settings.settingsStore.serverName);
                mw.Write<long>(Server.GetUniverseSize());

                QueueNetworkMessage(mw.GetMessageBytes());
                File.WriteAllBytes("/home/darklight/DUMP.txt", mw.GetMessageBytes());
            }
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
            Array.Copy(data, 0, messageBytes, 0, data.Length);
            lock (sendMessages)
            {
                sendMessages.Enqueue(messageBytes);
            }
            sendEvent.Set();
        }
    }
}

