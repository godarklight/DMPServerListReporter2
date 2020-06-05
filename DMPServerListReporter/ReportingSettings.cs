using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Xml;
using DarkMultiPlayerServer;

namespace DMPServerListReporter
{
    public class ReportingSettings
    {
        public List<string> reportingEndpoint = new List<string>();
        public string serverHash = "";
        public string gameAddress = "";
        public string banner = "";
        public string homepage = "";
        public string admin = "";
        public string team = "";
        public string location = "";
        public bool fixedIP = false;
        public string description = "";

        public void LoadSettings(string oldFile, string newFile, string tokenFile, string descriptionFile)
        {
            string oldSettingsFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), oldFile);
            string newSettingsFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), newFile);
            string tokenFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), tokenFile);
            string descriptionFileFullPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), descriptionFile);
            LoadToken(tokenFileFullPath);
            if (File.Exists(oldSettingsFileFullPath))
            {
                DarkMultiPlayerServer.DarkLog.Debug("Upgraded reporting settings file");
                LoadOldSettings(oldSettingsFileFullPath, descriptionFileFullPath);
                reportingEndpoint.Add("godarklight.info.tm:9001");
                SaveXMLSettings(newSettingsFileFullPath);
                File.Delete(oldSettingsFileFullPath);
            }
            else
            {
                if (!File.Exists(newSettingsFileFullPath))
                {
                    reportingEndpoint.Add("server.game.api.d-mp.org:9001");
                    reportingEndpoint.Add("godarklight.info.tm:9001");
                    reportingEndpoint.Add("ksp-dmp.sundevil.pl:12401");
                    SaveXMLSettings(newSettingsFileFullPath);
                }
                LoadXMLSettings(newSettingsFileFullPath);
            }
            LoadDescription(descriptionFileFullPath);
        }

        private void LoadToken(string tokenFile)
        {
            if (!File.Exists(tokenFile))
            {
                File.WriteAllText(tokenFile, Guid.NewGuid().ToString());
            }
            string tokenText = File.ReadAllText(tokenFile);
            this.serverHash = Main.CalculateSHA256Hash(tokenText);
        }

        private void LoadDescription(string descriptionFile)
        {
            if (!File.Exists(descriptionFile))
            {
                File.WriteAllText(descriptionFile, "");
            }
            this.description = File.ReadAllText(descriptionFile);
        }


        private void LoadXMLSettings(string settingsFile)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(settingsFile);

            gameAddress = xmlDoc.DocumentElement.GetElementsByTagName("gameAddress")[0].InnerText;
            banner = xmlDoc.DocumentElement.GetElementsByTagName("banner")[0].InnerText;
            homepage = xmlDoc.DocumentElement.GetElementsByTagName("homepage")[0].InnerText;
            admin = xmlDoc.DocumentElement.GetElementsByTagName("admin")[0].InnerText;
            team = xmlDoc.DocumentElement.GetElementsByTagName("team")[0].InnerText;
            location = xmlDoc.DocumentElement.GetElementsByTagName("location")[0].InnerText;
            fixedIP = Boolean.Parse(xmlDoc.DocumentElement.GetElementsByTagName("fixedIP")[0].InnerText);

            reportingEndpoint.Clear();
            foreach (XmlNode endpointNode in xmlDoc.DocumentElement.GetElementsByTagName("reporting"))
            {
                reportingEndpoint.Add(endpointNode.InnerText);
            }
        }

        private void SaveXMLSettings(string settingsFile)
        {
            string newFile = settingsFile + ".new";
            if (File.Exists(newFile))
            {
                File.Delete(newFile);
            }

            XmlWriterSettings xws = new XmlWriterSettings();
            xws.OmitXmlDeclaration = true;
            xws.Indent = true;

            using (XmlWriter xw = XmlWriter.Create(newFile, xws))
            {
                xw.WriteStartDocument();
                xw.WriteStartElement("settings");
                xw.WriteComment("All settings are optional - The defaults will work, although you should set gameAddress to a DNS name");
                xw.WriteComment("Take note that the server name and port are automatically detected from the DMPServer settings file");
                xw.WriteComment("gameAddress - The domain name / IP address shown on the server list");
                xw.WriteComment("If this is unset, the server list will automatically use your current public IP address");
                xw.WriteStartElement("gameAddress");
                xw.WriteString(gameAddress);
                xw.WriteFullEndElement();
                xw.WriteComment("banner - A HTTP address of your server picture");
                xw.WriteStartElement("banner");
                xw.WriteString(banner);
                xw.WriteFullEndElement();
                xw.WriteComment("homepage - A HTTP address link to your server website");
                xw.WriteStartElement("homepage");
                xw.WriteString(homepage);
                xw.WriteFullEndElement();
                xw.WriteComment("admin - The player that runs the server");
                xw.WriteStartElement("admin");
                xw.WriteString(admin);
                xw.WriteFullEndElement();
                xw.WriteComment("team - The group that runs the server");
                xw.WriteStartElement("team");
                xw.WriteString(team);
                xw.WriteFullEndElement();
                xw.WriteComment("location - The two letter country code of the server. Autodetected if left blank");
                xw.WriteStartElement("location");
                xw.WriteString(location);
                xw.WriteFullEndElement();
                xw.WriteComment("fixedIP - wether the server has a fixed address, true/false.");
                xw.WriteElementString("fixedIP", fixedIP.ToString().ToLower());
                xw.WriteComment("Specify DMPServerList report receivers to connect to. You may use dns:port, ipv4:port, or [ipv6]:port format");
                xw.WriteComment("These defaults provide automatic failover, and the receivers sync with each other, so connecting to any of them is fine");
                foreach (string remoteAddress in reportingEndpoint)
                {
                    xw.WriteElementString("reporting", remoteAddress);
                }
                xw.WriteEndElement();
                xw.WriteEndDocument();
            }
            File.Move(newFile, settingsFile);
        }

        private void LoadOldSettings(string settingsFile, string descriptionFile)
        {
            using (StreamReader sr = new StreamReader(settingsFile))
            {
                bool readingDescription = false;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                string currentLine;

                while ((currentLine = sr.ReadLine()) != null)
                {
                    if (!readingDescription)
                    {
                        try
                        {
                            string key = currentLine.Substring(0, currentLine.IndexOf("=")).Trim();
                            string value = currentLine.Substring(currentLine.IndexOf("=") + 1).Trim();
                            switch (key)
                            {
                                case "reporting":
                                    reportingEndpoint.Add(value);
                                    break;
                                case "gameAddress":
                                    gameAddress = value;
                                    break;
                                case "banner":
                                    banner = value;
                                    break;
                                case "homepage":
                                    homepage = value;
                                    break;
                                case "admin":
                                    admin = value;
                                    break;
                                case "team":
                                    team = value;
                                    break;
                                case "location":
                                    location = value;
                                    break;
                                case "fixedIP":
                                    fixedIP = (value == "true");
                                    break;
                                case "description":
                                    readingDescription = true;
                                    sb.AppendLine(value);
                                    break;
                            }
                        }
                        catch (Exception e)
                        {
                            DarkLog.Error("Error reading settings file, Exception " + e);
                        }
                    }
                    else
                    {
                        //Reading description
                        sb.AppendLine(currentLine);
                    }
                }
                description = sb.ToString();
                if (!File.Exists(descriptionFile))
                {
                    File.WriteAllText(descriptionFile, description);
                }
            }
        }
    }
}