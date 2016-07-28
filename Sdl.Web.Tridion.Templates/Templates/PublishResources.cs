﻿using System;
using System.Text;
using Sdl.Web.Tridion.Common;
using System.Collections.Generic;
using Tridion.ContentManager.CommunicationManagement;
using Tridion.ContentManager.ContentManagement;
using Tridion.ContentManager.ContentManagement.Fields;
using Tridion.ContentManager.Templating;
using Tridion.ContentManager.Templating.Assembly;

namespace Sdl.Web.Tridion.Templates
{
    /// <summary>
    /// Publish resource JSON files (one per module). A module configuration can link to 
    /// multiple resource components - these are merged into a single JSON file
    /// </summary>
    [TcmTemplateTitle("Publish Resources")]
    public class PublishResources : TemplateBase
    {
        // json content in page
        private const string JsonOutputFormat = "{{\"name\":\"Publish Resources\",\"status\":\"Success\",\"files\":[{0}]}}";

        public override void Transform(Engine engine, Package package)
        {
            Initialize(engine, package);
            
            //The core configuration component should be the one being processed by the template
            Component coreConfigComponent = GetComponent();
            StructureGroup sg = GetSystemStructureGroup("resources");
            //_moduleRoot = GetModulesRoot(coreConfigComponent);

            //Get all the active modules
            Dictionary<string, Component> moduleComponents = GetActiveModules();
            List<string> filesCreated = new List<string>();
            
            //For each active module, publish the config and add the filename(s) to the bootstrap list
            foreach (KeyValuePair<string, Component> module in moduleComponents)
            {
                filesCreated.Add(ProcessModule(module.Key, module.Value, sg));
            }

            //Publish the boostrap list, this is used by the web application to load in all other resource files
            PublishBootstrapJson(filesCreated, coreConfigComponent, sg, "resource-");

            StringBuilder publishedFiles = new StringBuilder();
            foreach (string file in filesCreated)
            {
                if (!String.IsNullOrEmpty(file))
                {
                    publishedFiles.AppendCommaSeparated(file);
                    Logger.Info("Published " + file);                    
                }
            }

            // append json result to output
            string json = String.Format(JsonOutputFormat, publishedFiles);
            Item outputItem = package.GetByName(Package.OutputName);
            if (outputItem != null)
            {
                package.Remove(outputItem);
                string output = outputItem.GetAsString();
                if (output.StartsWith("["))
                {
                    // insert new json object
                    json = String.Format("{0},{1}{2}]", output.TrimEnd(']'), Environment.NewLine, json);
                }
                else
                {
                    // append new json object
                    json = String.Format("[{0},{1}{2}]", output, Environment.NewLine, json);
                }
            }
            package.PushItem(Package.OutputName, package.CreateStringItem(ContentType.Text, json));
        }

        protected string ProcessModule(string moduleName, Component module, StructureGroup sg)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            ItemFields fields = new ItemFields(module.Content, module.Schema);

            foreach (Component configComp in fields.GetComponentValues("resource"))
            {
                data = MergeData(data, ReadComponentData(configComp));
            }
            return PublishJsonData(data, module, moduleName, "resource", sg);
        }
    }
}
