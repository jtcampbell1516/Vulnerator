﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using log4net;
using System.Xml;
using Vulnerator.Model.DataAccess;
using Vulnerator.Model.ModelHelper;
using Vulnerator.Model.Object;

namespace Vulnerator.Model.BusinessLogic
{
    /// <summary>
    /// Class housing all items required to parse ACAS *.nessus scan files.
    /// </summary>
    public class AcasNessusReader
    {
        private Assembly assembly = Assembly.GetExecutingAssembly();
        DatabaseInterface databaseInterface = new DatabaseInterface();
        private int hardwarePrimaryKey = 0;
        private int softwarePrimaryKey = 0;
        private int lastVulnerabilitySourceId = 0;
        private int lastVulnerabilityId = 0;
        private int groupPrimaryKey = 0;
        private int sourceFilePrimaryKey = 0;
        private static readonly ILog log = LogManager.GetLogger(typeof(Logger));
        private List<string> cves = new List<string>();
        private List<string> cpes = new List<string>();
        private List<string> xrefs = new List<string>();
        private string[] vulnerabilityColumns = SetColumnArrays("Vulnerabilities");
        private string[] uniqueFindingsColumns = SetColumnArrays("UniqueFindings");
        /// <summary>
        /// Reads *.nessus files exported from within ACAS and writes the results to the appropriate DataTables.
        /// </summary>
        /// <param name="fileName">Name of *.nessus file to be parsed.</param>
        /// <param name="mitigationsList">List of mitigation items for vulnerabilities to be read against.</param>
        /// <param name="systemName">Name of the system that the mitigations check will be run against.</param>
        /// <returns>string Value</returns>
        public string ReadAcasNessusFile(Object.File file)
        {
            try
            {                
                if (file.FilePath.IsFileInUse())
                {
                    log.Error(file.FileName + " is in use; please close any open instances and try again.");
                    return "Failed; File In Use";
                }

                ParseNessusWithXmlReader(file);

                return "Processed";
            }
            catch (Exception exception)
            {
                log.Error("Unable to process Nessus file.");
                log.Debug("Exception details:", exception);
                return "Failed; See Log";
            }
        }

        private void ParseNessusWithXmlReader(Object.File file)
        {
            try
            {
                using (SQLiteTransaction sqliteTransaction = FindingsDatabaseActions.sqliteConnection.BeginTransaction())
                {
                    using (SQLiteCommand sqliteCommand = DatabaseBuilder.sqliteConnection.CreateCommand())
                    {
                        InsertGroup(sqliteCommand, file);
                        InsertSourceFile(sqliteCommand, file);
                        XmlReaderSettings xmlReaderSettings = GenerateXmlReaderSettings();
                        using (XmlReader xmlReader = XmlReader.Create(file.FilePath, xmlReaderSettings))
                        {
                            WorkingSystem workingSystem = new WorkingSystem();
                            while (xmlReader.Read())
                            {
                                if (xmlReader.IsStartElement())
                                {
                                    switch (xmlReader.Name)
                                    {
                                        case "ReportHost":
                                            {
                                                ParseHostData(sqliteCommand, xmlReader);
                                                break;
                                            }
                                        case "ReportItem":
                                            {
                                                ParseVulnerability(sqliteCommand, xmlReader);
                                                break;
                                            }
                                        default:
                                            { break; }
                                    }
                                }
                                else if (xmlReader.NodeType == XmlNodeType.EndElement && xmlReader.Name.Equals("ReportHost"))
                                { ClearGlobals(); }
                            }
                        }
                    }
                    sqliteTransaction.Commit();
                }
            }
            catch (Exception exception)
            {
                log.Error("Unable to parse nessus file with XmlReader.");
                throw exception;
            }
        }

        private void InsertGroup(SQLiteCommand sqliteCommand, Object.File file)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(file.FileSystemName))
                { sqliteCommand.Parameters.Add(new SQLiteParameter("Group_Name", file.FileSystemName)); }
                else
                { sqliteCommand.Parameters.Add(new SQLiteParameter("Group_Name", "Unassigned")); }
                sqliteCommand.CommandText = Properties.Resources.SelectGroup;
                if (int.TryParse(Convert.ToString(sqliteCommand.ExecuteScalar()), out groupPrimaryKey))
                {
                    sqliteCommand.Parameters.Clear();
                    return;
                }
                sqliteCommand.CommandText = Properties.Resources.InsertGroup;
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                groupPrimaryKey = int.Parse(sqliteCommand.ExecuteScalar().ToString());
                sqliteCommand.Parameters.Clear();
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to insert group \"{0}\" into database.", file.FileSystemName));
                throw exception;
            }
        }

        private void InsertSourceFile(SQLiteCommand sqliteCommand, Object.File file)
        {
            try
            {
                sqliteCommand.Parameters.Add(new SQLiteParameter("Finding_Source_File_Name", file.FileName));
                sqliteCommand.CommandText = Properties.Resources.SelectUniqueFindingSourceFile;
                if (int.TryParse(Convert.ToString(sqliteCommand.ExecuteScalar()), out sourceFilePrimaryKey))
                {
                    sqliteCommand.Parameters.Clear();
                    return;
                }
                sqliteCommand.CommandText = Properties.Resources.InsertUniqueFindingSource;
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.Parameters.Clear();
                sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                sourceFilePrimaryKey = int.Parse(sqliteCommand.ExecuteScalar().ToString());
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to insert unique finding source file \"{0}\".", file.FileName));
                throw exception;
            }
        }

        private void ParseHostData(SQLiteCommand sqliteCommand, XmlReader xmlReader)
        {
            string hostIp = xmlReader.GetAttribute("name");
            sqliteCommand.Parameters.Add(new SQLiteParameter("IP_Address", hostIp));
            try
            {
                while (xmlReader.Read())
                {
                    if (xmlReader.IsStartElement())
                    {
                        switch (xmlReader.GetAttribute("name"))
                        {
                            case "hostname":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Host_Name", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "operating-system":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Discovered_Software_Name", ObtainCurrentNodeValue(xmlReader)));
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Displayed_Software_Name", ObtainCurrentNodeValue(xmlReader)));
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Is_OS_Or_Firmware", "True"));
                                    break;
                                }
                            case "host-fqdn":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("FQDN", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "mac-address":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("MAC_Address", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "netbios-name":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("NetBIOS", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            default:
                                { break; }
                        }
                    }
                    else if (xmlReader.NodeType == XmlNodeType.EndElement && xmlReader.Name.Equals("HostProperties"))
                    {
                        InsertHardware(sqliteCommand);
                        sqliteCommand.Parameters.Add(new SQLiteParameter("Hardware_ID", hardwarePrimaryKey));
                        InsertAndMapMacAndIp(sqliteCommand);
                        if (sqliteCommand.Parameters.Contains("Discovered_Software_Name"))
                        {
                            InsertSoftware(sqliteCommand);
                            MapHardwareToSoftware(sqliteCommand);
                        }
                        sqliteCommand.Parameters.Clear();
                    }
                }
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to parse host \"{0}\".", hostIp));
                throw exception;
            }
        }

        private void InsertHardware(SQLiteCommand sqliteCommand)
        {
            try
            {
                if (!sqliteCommand.Parameters.Contains("NetBIOS"))
                { sqliteCommand.Parameters.Add(new SQLiteParameter("NetBIOS", "Undefined")); }
                if (!sqliteCommand.Parameters.Contains("Host_Name"))
                { sqliteCommand.Parameters.Add(new SQLiteParameter("Host_Name", "Undefined")); }
                if (!sqliteCommand.Parameters.Contains("FQDN"))
                { sqliteCommand.Parameters.Add(new SQLiteParameter("FQDN", "Undefined")); }
                sqliteCommand.Parameters.Add(new SQLiteParameter("Is_Virtual_Server", "False"));
                sqliteCommand.Parameters.Add(new SQLiteParameter("NIAP_Level", string.Empty));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Manufacturer", string.Empty));
                sqliteCommand.Parameters.Add(new SQLiteParameter("ModelNumber", string.Empty));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Is_IA_Enabled", string.Empty));
                sqliteCommand.Parameters.Add(new SQLiteParameter("SerialNumber", string.Empty));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Role", string.Empty));
                sqliteCommand.CommandText = Properties.Resources.InsertHardware;
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.CommandText = Properties.Resources.SelectHardware;
                hardwarePrimaryKey = int.Parse(sqliteCommand.ExecuteScalar().ToString());
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to insert host \"{0}\".", sqliteCommand.Parameters["IP_Address"].Value.ToString()));
                throw exception;
            }
        }

        private void InsertSoftware(SQLiteCommand sqliteCommand)
        { 
            try
            {
                sqliteCommand.CommandText = Properties.Resources.InsertAcasDiscoveredSoftware;
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.CommandText = Properties.Resources.SelectSoftware;
                softwarePrimaryKey = int.Parse(sqliteCommand.ExecuteScalar().ToString());
            }
            catch (Exception exception)
            {
                log.Error(string.Format(""));
                throw exception;
            }
        }

        private void MapHardwareToSoftware(SQLiteCommand sqliteCommand)
        { 
            try
            {
                sqliteCommand.CommandText = Properties.Resources.MapHardwareToSoftware;
                sqliteCommand.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                log.Error(string.Format(""));
                log.Debug("Exception details:", exception);
                throw exception;
            }
        }

        private void InsertAndMapMacAndIp(SQLiteCommand sqliteCommand)
        { 
            try
            {
                sqliteCommand.CommandText = Properties.Resources.InsertIpAddress;
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                int ipId = int.Parse(sqliteCommand.ExecuteScalar().ToString());
                sqliteCommand.CommandText = Properties.Resources.MapIpToHardware;
                sqliteCommand.Parameters.Add(new SQLiteParameter("IP_Address_ID", ipId));
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.CommandText = Properties.Resources.InsertMacAddress;
                sqliteCommand.ExecuteNonQuery();
                sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                int macId = int.Parse(sqliteCommand.ExecuteScalar().ToString());
                sqliteCommand.CommandText = Properties.Resources.MapIpToHardware;
                sqliteCommand.Parameters.Add(new SQLiteParameter("MAC_Address_ID", macId));
                sqliteCommand.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                log.Error(string.Format(""));
                log.Debug("Exception details:", exception);
                throw exception;
            }
        }

        private void ParseVulnerability(SQLiteCommand sqliteCommand, XmlReader xmlReader)
        {
            string pluginId = xmlReader.GetAttribute("pluginID");
            try
            {
                sqliteCommand.Parameters.Add(new SQLiteParameter("Port", xmlReader.GetAttribute("port")));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Protocol", xmlReader.GetAttribute("protocol")));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Service", xmlReader.GetAttribute("svc_name")));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Unique_Vulnerability_Identifier", pluginId));
                sqliteCommand.Parameters.Add(new SQLiteParameter("Vulnerability_Title", xmlReader.GetAttribute("pluginName")));
                sqliteCommand.Parameters.Add(new SQLiteParameter("VulnerabilityFamilyOrClass", xmlReader.GetAttribute("pluginFamily")));
                while (xmlReader.Read())
                {
                    if (xmlReader.IsStartElement())
                    {
                        switch (xmlReader.Name)
                        {
                            case "description":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Description", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "plugin_modification_date":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Modified_Date", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "plugin_publication_date":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Published_Date", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "patch_publication_date":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Fix_Published_Date", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "risk_factor":
                                {
                                    xmlReader.Read();
                                    if (!sqliteCommand.Parameters.Contains("Raw_Risk"))
                                    { sqliteCommand.Parameters.Add(new SQLiteParameter("Raw_Risk", ConvertRiskFactorToRawRisk(xmlReader.Value))); }
                                    break;
                                }
                            case "solution":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Fix_Text", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "synopsis":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Risk_Statement", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "plugin_output":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("Tool_Generated_Output", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "stig_severity":
                                {
                                    if (!sqliteCommand.Parameters.Contains("Raw_Risk"))
                                    { sqliteCommand.Parameters.Add(new SQLiteParameter("Raw_Risk", ObtainCurrentNodeValue(xmlReader))); }
                                    else
                                    { sqliteCommand.Parameters["Raw_Risk"].Value = ObtainCurrentNodeValue(xmlReader); }
                                    break;
                                }
                            case "xref":
                                {
                                    xrefs.Add(ObtainCurrentNodeValue(xmlReader));
                                    break;
                                }
                            case "cve":
                                {
                                    cves.Add(ObtainCurrentNodeValue(xmlReader));
                                    break;
                                }
                            case "cpe":
                                {
                                    cpes.Add(ObtainCurrentNodeValue(xmlReader));
                                    break;
                                }
                            case "bid":
                                {
                                    xrefs.Add(string.Format("BID:{0}", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "cvss_base_score":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("CVSS_Base_Score", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "cvss_temporal_score":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("CVSS_Temporal_Score", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "cvss_vector":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("CVSS_Base_Vector", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            case "cvss_temporal_vector":
                                {
                                    sqliteCommand.Parameters.Add(new SQLiteParameter("CVSS_Temporal_Vector", ObtainCurrentNodeValue(xmlReader)));
                                    break;
                                }
                            default:
                                { break; }
                        }
                    }
                    else if (xmlReader.NodeType == XmlNodeType.EndElement && xmlReader.Name.Equals("ReportItem"))
                    {
                        sqliteCommand.Parameters.Add(new SQLiteParameter("Hardware_ID", hardwarePrimaryKey));
                        sqliteCommand.Parameters.Add(new SQLiteParameter("Vulnerabiity_Source_ID", lastVulnerabilitySourceId));
                        InsertVulnerability(sqliteCommand);
                        InsertAndMapPort(sqliteCommand);
                        InsertUniqueFinding(sqliteCommand);
                        sqliteCommand.Parameters.Clear();
                        InsertAndMapReferences(sqliteCommand, cpes, "CPE");
                        InsertAndMapReferences(sqliteCommand, cves, "CVE");
                        InsertAndMapReferences(sqliteCommand, xrefs, "Multi");
                        ClearGlobals();
                    }
                }
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to parse plugin \"{0}\".", pluginId));
                throw exception;
            }
        }

        private void InsertVulnerability(SQLiteCommand sqliteCommand)
        { 
            try
            {
                SQLiteCommand clonedCommand = (SQLiteCommand)sqliteCommand.Clone();
                clonedCommand.CommandText = Properties.Resources.SelectVulnerability;
                using (SQLiteDataReader sqliteDataReader = clonedCommand.ExecuteReader())
                {
                    if (sqliteDataReader.HasRows)
                    {
                        while (sqliteDataReader.Read())
                        {
                            lastVulnerabilityId = int.Parse(sqliteDataReader["Vulnerability_ID"].ToString());
                            sqliteCommand.Parameters.Add(new SQLiteParameter("Vulnerability_ID", lastVulnerabilityId));
                            sqliteCommand.CommandText = Properties.Resources.UpdateVulnerability;
                            sqliteCommand.ExecuteNonQuery();
                            return;
                        }
                    }
                    sqliteCommand.CommandText = Properties.Resources.InsertVulnerability;
                    foreach (SQLiteParameter parameter in sqliteCommand.Parameters)
                    {
                        if (vulnerabilityColumns.Contains(parameter.ParameterName))
                        {
                            if (sqliteCommand.Parameters.IndexOf(parameter) == 0)
                            { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(39, "@" + parameter.ParameterName); }
                            else
                            { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(39, "@" + parameter.ParameterName + ", "); }
                        }
                    }
                    foreach (SQLiteParameter parameter in sqliteCommand.Parameters)
                    {
                        if (vulnerabilityColumns.Contains(parameter.ParameterName))
                        {
                            if (sqliteCommand.Parameters.IndexOf(parameter) == 0)
                            { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(29, parameter.ParameterName); }
                            else
                            { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(29, parameter.ParameterName + ", "); }
                        }
                    }
                    sqliteCommand.ExecuteNonQuery();
                    sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                    lastVulnerabilityId = int.Parse(sqliteCommand.ExecuteScalar().ToString());
                }
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to insert plugin \"{0}\" into Vulnerabilities.", sqliteCommand.Parameters["Unique_Vulnerability_Identifier"].Value.ToString()));
                throw exception;
            }
        }

        private void InsertAndMapPort(SQLiteCommand sqliteCommand)
        {
            try
            {
                int ppsId = 0;
                SQLiteCommand clonedCommand = (SQLiteCommand)sqliteCommand.Clone();
                clonedCommand.CommandText = Properties.Resources.SelectPort;
                using (SQLiteDataReader sqliteDataReader = clonedCommand.ExecuteReader())
                {
                    if (!sqliteDataReader.HasRows)
                    {
                        sqliteCommand.CommandText = Properties.Resources.InsertPort;
                        sqliteCommand.ExecuteNonQuery();
                        sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                        ppsId = int.Parse(sqliteCommand.ExecuteScalar().ToString());
                    }
                    else
                    {
                        while (sqliteDataReader.Read())
                        { ppsId = int.Parse(sqliteDataReader["PPS_ID"].ToString()); }
                    }
                }
                sqliteCommand.Parameters.Add(new SQLiteParameter("Hardware_ID", hardwarePrimaryKey));
                sqliteCommand.Parameters.Add(new SQLiteParameter("PPS_ID", ppsId));
                sqliteCommand.CommandText = Properties.Resources.MapPortToHardware;
                sqliteCommand.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to insert or map port \"{0} {1}\".", 
                    sqliteCommand.Parameters["Protocol"].Value.ToString(),
                    sqliteCommand.Parameters["Port"].Value.ToString()));
                throw exception;
            }
        }

        private void InsertUniqueFinding(SQLiteCommand sqliteCommand)
        { 
            try
            {
                SQLiteCommand clonedCommand = (SQLiteCommand)sqliteCommand.Clone();
                clonedCommand.CommandText = Properties.Resources.SelectUniqueFinding;
                using (SQLiteDataReader sqliteDataReader = clonedCommand.ExecuteReader())
                {
                    if (sqliteDataReader.HasRows)
                    {
                        sqliteCommand.CommandText = Properties.Resources.UpdateAcasUniqueFinding;
                        sqliteCommand.Parameters.Add(new SQLiteParameter("Unique_Finding_ID", sqliteDataReader["Unique_Finding_ID"]));
                        sqliteCommand.ExecuteNonQuery();
                        return;
                    }
                }
                sqliteCommand.CommandText = Properties.Resources.InsertUniqueFinding;
                sqliteCommand.Parameters.Add(new SQLiteParameter("Unique_Finding_ID", DBNull.Value));
                foreach (SQLiteParameter parameter in sqliteCommand.Parameters)
                {
                    if (uniqueFindingsColumns.Contains(parameter.ParameterName))
                    {
                        if (sqliteCommand.Parameters.IndexOf(parameter) == 0)
                        { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(38, "@" + parameter.ParameterName); }
                        else
                        { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(38, "@" + parameter.ParameterName + ", "); }
                    }
                }
                foreach (SQLiteParameter parameter in sqliteCommand.Parameters)
                {
                    if (uniqueFindingsColumns.Contains(parameter.ParameterName))
                    {
                        if (sqliteCommand.Parameters.IndexOf(parameter) == 0)
                        { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(28, parameter.ParameterName); }
                        else
                        { sqliteCommand.CommandText = sqliteCommand.CommandText.Insert(28, parameter.ParameterName + ", "); }
                    }
                }
                sqliteCommand.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to associate plugin \"{0}\" with \"{1}\".",
                    sqliteCommand.Parameters["Unique_Vulnerability_Identifier"].Value.ToString(),
                    sqliteCommand.Parameters["Hardware_ID"].Value.ToString()));
                throw exception;
            }
        }

        private void InsertAndMapReferences(SQLiteCommand sqliteCommand, List<string> references, string referenceType)
        { 
            try
            {
                foreach (string reference in references)
                {
                    sqliteCommand.Parameters.Add(new SQLiteParameter("Vulnerability_ID", lastVulnerabilityId));
                    sqliteCommand.CommandText = Properties.Resources.InsertVulnerabilityReference;
                    if (!referenceType.Equals("CVE") || !referenceType.Equals("CPE"))
                    {
                        referenceType = reference.Split(':')[0];
                        sqliteCommand.Parameters.Add(new SQLiteParameter("Reference", reference.Split(':')[1]));
                    }
                    else
                    { sqliteCommand.Parameters.Add(new SQLiteParameter("Reference", reference)); }
                    sqliteCommand.Parameters.Add(new SQLiteParameter("Reference_Type", referenceType));
                    sqliteCommand.ExecuteNonQuery();
                    sqliteCommand.CommandText = "SELECT last_insert_rowid();";
                    int referenceId = int.Parse(sqliteCommand.ExecuteScalar().ToString());
                    sqliteCommand.Parameters.Add(new SQLiteParameter("Reference_ID", referenceId));
                    sqliteCommand.CommandText = Properties.Resources.MapReferenceToVulnerability;
                    sqliteCommand.ExecuteNonQuery();
                    sqliteCommand.Parameters.Clear();
                }
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to insert / map reference."));
                throw exception;
            }
        }

        private string ObtainCurrentNodeValue(XmlReader xmlReader)
        {
            try
            {
                xmlReader.Read();
                string value = xmlReader.Value;
                value = value.Replace("&gt", ">");
                value = value.Replace("&lt", "<");
                return value;
            }
            catch (Exception exception)
            {
                log.Error("Unable to obtain currently accessed node value.");
                throw exception;
            }
        }

        private void ClearGlobals()
        {
            hardwarePrimaryKey = 0;
            softwarePrimaryKey = 0;
            lastVulnerabilityId = 0;
            cves.Clear();
            cpes.Clear();
            xrefs.Clear();
    }

        private static string[] SetColumnArrays(string tableName)
        { 
            try
            {
                switch (tableName)
                {
                    case "Vulnerabilities":
                        {
                            string[] value = new string[] 
                            {
                                "Unique_Vulnerability_Identifier", "VulnerabilityFamilyOrClass", "Vulnerability_Title", "Description", "Risk_Statement",
                                "Fix_Text", "Published_Date", "Modified_Date", "Fix_Published_Date", "Raw_Risk", "CVSS_Base_Score", "CVSS_Base_Vector",
                                "CVSS_Temporal_Score", "CVSS_Temporal_Vector", "Check_Content", "Overflow"
                            };
                            return value;
                        }
                    case "UniqueFindings":
                        {
                            string[] value = new string[] 
                            {
                                "Tool_Generated_Output", "Severity", "First_Discovered", "Last_Observed", "Approval_Status", "Delta_Analysis_Required",
                                "Finding_Type_ID", "Source_File_ID", "Status", "Unique_Finding_ID"
                            };
                            return value;
                        }
                    default:
                        { throw new Exception("Invalid table name."); }
                }
            }
            catch (Exception exception)
            {
                log.Error(string.Format(""));
                throw exception;
            }
        }

        private XmlReaderSettings GenerateXmlReaderSettings()
        {
            try
            {
                XmlReaderSettings xmlReaderSettings = new XmlReaderSettings();
                xmlReaderSettings.IgnoreWhitespace = true;
                xmlReaderSettings.IgnoreComments = true;
                xmlReaderSettings.ValidationType = ValidationType.Schema;
                xmlReaderSettings.ValidationFlags = System.Xml.Schema.XmlSchemaValidationFlags.ProcessInlineSchema;
                xmlReaderSettings.ValidationFlags = System.Xml.Schema.XmlSchemaValidationFlags.ProcessSchemaLocation;
                return xmlReaderSettings;
            }
            catch (Exception exception)
            {
                log.Error("Unable to generate XmlReaderSettings.");
                throw exception;
            }
        }

        private string ConvertRiskFactorToRawRisk(string riskFactor)
        { 
            try
            {
                switch (riskFactor)
                {
                    case "None":
                        { return "IV"; }
                    case "Low":
                        { return "III"; }
                    case "Medium":
                        { return "II"; }
                    case "High":
                        { return "I"; }
                    case "Critical":
                        { return "I"; }
                    default:
                        { return "Unknown"; }
                }
            }
            catch (Exception exception)
            {
                log.Error(string.Format("Unable to convert \"{0}\" to a standardized raw risk.", riskFactor));
                throw exception;
            }
        }
    }
}
