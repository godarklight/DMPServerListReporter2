using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
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
        private const string TOKEN_FILE = "ReportingToken.txt";
        private const string SETTINGS_FILE = "ReportingSettings.txt";
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
        //Settings
        ReportingSettings settingsStore = new ReportingSettings();
        //Client state tracking
        List<string> connectedPlayers = new List<string>();

        public Main()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            string tokenFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), TOKEN_FILE);
            if (!File.Exists(tokenFileFullPath))
            {
                using (StreamWriter sw = new StreamWriter(tokenFileFullPath))
                {
                    sw.WriteLine(Guid.NewGuid().ToString());
                }
            }
            using (StreamReader sr = new StreamReader(tokenFileFullPath))
            {
                settingsStore.serverHash = CalculateSHA256Hash(sr.ReadLine());
            }
            string settingsFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), SETTINGS_FILE);
            if (!File.Exists(settingsFileFullPath))
            {
                using (StreamWriter sw = new StreamWriter(settingsFileFullPath))
                {
                    sw.WriteLine("reporting = server.game.api.d-mp.org:9001");
                    sw.WriteLine("gameAddress = ");
                    sw.WriteLine("banner = ");
                    sw.WriteLine("homepage = ");
                    sw.WriteLine("admin = ");
                    sw.WriteLine("team = ");
                    sw.WriteLine("location = ");
                    sw.WriteLine("fixedIP = false");
                    sw.WriteLine("description = ");
                }
            }
            bool reloadSettings = false;
            using (StreamReader sr = new StreamReader(settingsFileFullPath))
            {
                string currentLine;

                while ((currentLine = sr.ReadLine()) != null)
                {
                    try
                    {
                        string key = currentLine.Substring(0, currentLine.IndexOf("=")).Trim();
                        string value = currentLine.Substring(currentLine.IndexOf("=") + 1).Trim();
                        switch (key)
                        {
                            case "reporting":
                                {
                                    string address = value.Substring(0, value.LastIndexOf(":"));
                                    string port = value.Substring(value.LastIndexOf(":") + 1);
                                    IPAddress reportingIP;
                                    int reportingPort = 0;
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
                                            settingsStore.reportingEndpoint = new IPEndPoint(reportingIP, reportingPort);
                                        }
                                    }
                                }
                                break;
                            case "gameAddress":
                                settingsStore.gameAddress = value;
                                break;
                            case "banner":
                                settingsStore.banner = value;
                                break;
                            case "homepage":
                                settingsStore.homepage = value;
                                break;
                            case "admin":
                                settingsStore.admin = value;
                                break;
                            case "team":
                                settingsStore.team = value;
                                break;
                            case "location":
                                settingsStore.location = value;
                                break;
                            case "fixedIP":
                                settingsStore.fixedIP = (value == "true");
                                break;
                            case "description":
                                settingsStore.description = value;
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        DarkLog.Error("Error reading settings file, Exception " + e);
                    }
                }
            }
            /*
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
            */
            if (reloadSettings)
            {
                //Load with the default settings if anything is incorrect.
                File.Delete(settingsFileFullPath);
                LoadSettings();
            }
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
            connectedPlayers.Add(client.playerName);
            ReportData();
        }

        public override void OnClientDisconnect(ClientObject client)
        {
            connectedPlayers.Remove(client.playerName);
            ReportData();
        }

        private void ConnectToServer()
        {
            try
            {
                connection = new TcpClient();
                connection.Connect(settingsStore.reportingEndpoint);
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
                mw.Write<string>(settingsStore.serverHash);
                mw.Write<string>(Settings.settingsStore.serverName);
                mw.Write<string>(settingsStore.description);
                mw.Write<int>(Settings.settingsStore.port);
                mw.Write<string>(settingsStore.gameAddress);
                mw.Write<int>(DarkMultiPlayerCommon.Common.PROTOCOL_VERSION);
                mw.Write<string>(DarkMultiPlayerCommon.Common.PROGRAM_VERSION);
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
                mw.Write<string[]>(GetServerPlayerArray());
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

