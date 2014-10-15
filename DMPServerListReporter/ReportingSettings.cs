using System;
using System.Net;
using System.Net.Sockets;

namespace DMPServerListReporter
{
    public class ReportingSettings
    {
        public IPEndPoint reportingEndpoint;
        public string serverHash;
        public string gameAddress;
        public string banner;
        public string homepage;
        public string admin;
        public string team;
        public string location;
        public bool fixedIP;
        public string description;
    }
}

