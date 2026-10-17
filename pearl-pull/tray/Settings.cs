using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PearlPull.Tray
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public string Address { get; set; }
        [DataMember] public int Port { get; set; }
        [DataMember] public string PairingCode { get; set; }
        [DataMember] public string StreamDeckToken { get; set; }

        public Settings() { Port = Protocol.DefaultPort; }
        public static string DirectoryPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinecraftUtils"); } }
        public static string PathFor(bool receiver) { return Path.Combine(DirectoryPath, "pearl-pull-" + (receiver ? "receiver" : "sender") + ".json"); }
        public void Validate(bool receiver)
        {
            Protocol.ParseKey(PairingCode);
            if (Port < 1024 || Port > 65535) throw new ArgumentException("The network port must be between 1024 and 65535.");
            IPAddress parsed;
            if (!receiver && (!IPAddress.TryParse(Address, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork))
                throw new ArgumentException("Enter PC 2's home IPv4 address, such as 192.168.4.48.");
            if (!string.IsNullOrEmpty(StreamDeckToken)) Protocol.ParseKey(StreamDeckToken);
        }
        public static Settings Load(bool receiver)
        {
            return LoadFrom(PathFor(receiver), receiver);
        }
        public static Settings LoadFrom(string path, bool receiver)
        {
            if (!File.Exists(path)) return null;
            // ReadAllText removes the BOM written by Windows PowerShell 5.1.
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
            {
                Settings value = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                value.Validate(receiver);
                return value;
            }
        }
        public void Save(bool receiver)
        {
            SaveTo(PathFor(receiver), receiver);
        }
        public void SaveTo(string path, bool receiver)
        {
            Validate(receiver);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public string NeoUrl { get { return "http://127.0.0.1:48124/pull/" + StreamDeckToken; } }
    }
}
