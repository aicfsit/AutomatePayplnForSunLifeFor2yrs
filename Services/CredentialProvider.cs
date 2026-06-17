using System.IO;
using Newtonsoft.Json;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    public class CredentialProvider
    {
        public AppConfig Load(string path)
        {
            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<AppConfig>(json);
        }
    }
}
