﻿using System.Globalization;
using System.Text;
using Sdl.Web.Tridion.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Tridion.ContentManager;
using Tridion.ContentManager.CommunicationManagement;
using Tridion.ContentManager.ContentManagement;
using Tridion.ContentManager.ContentManagement.Fields;
using Tridion.ContentManager.Publishing.Rendering;
using Tridion.ContentManager.Templating;
using Tridion.ContentManager.Templating.Assembly;

namespace Sdl.Web.Tridion.Templates
{
    /// <summary>
    /// Publishes site configuration as JSON files. Multiple configuration components can 
    /// be linked to a module configuration. The values in these are merged into a single
    /// configuration file per module. There are also JSON files published containing schema and template
    /// information (1 per module) and a general taxonomies configuration json file
    /// </summary>
    [TcmTemplateTitle("Publish Configuration")]
    public class PublishConfiguration : TemplateBase
    {
        // json content in page
        private const string JsonOutputFormat = "{{\"name\":\"Publish Configuration\",\"status\":\"Success\",\"files\":[{0}]}}";

        private const string TemplateConfigName = "templates";
        private const string SchemasConfigName = "schemas";
        private const string TaxonomiesConfigName = "taxonomies";

        private const string LocalizationConfigComponentTitle = "Localization Configuration";
        private const string EnvironmentConfigComponentTitle = "Environment Configuration";
        private const string SearchConfigComponentTitle = "Search Configuration";
        private const string CmsUrlKey = "cmsurl";
        private const string SearchQueryUrlKey = "queryURL";
        private const string StagingSearchIndexKey = "stagingIndexConfig";
        private const string LiveSearchIndexKey = "liveIndexConfig";

        private StructureGroup _configStructureGroup;
        private Component _localizationConfigurationComponent;

        public override void Transform(Engine engine, Package package)
        {
            Initialize(engine, package);
            
            //The core configuration component should be the one being processed by the template
            Component coreConfigComponent = GetComponent();
            _configStructureGroup = GetSystemStructureGroup("config");

            // Determine the active modules
            Dictionary<string, Component> modules = GetActiveModules();
            List<string> filesCreated = new List<string>();

            //For each active module, publish the config and add the filename(s) to the bootstrap list
            foreach (KeyValuePair<string, Component> module in modules)
            {
                string moduleName = module.Key;
                Component moduleConfigComponent = module.Value;
                Folder moduleFolder = GetModuleFolder(moduleConfigComponent);

                string moduleConfigFileName = PublishModuleConfig(moduleName, moduleConfigComponent);
                filesCreated.Add(moduleConfigFileName);
                Binary moduleSchemasConfig = PublishModuleSchemasConfig(moduleName, moduleFolder, moduleConfigComponent);
                if (moduleSchemasConfig != null)
                {
                    filesCreated.Add(JsonSerialize(moduleSchemasConfig.Url));
                }
                Binary moduleTemplatesConfig = PublishModuleTemplatesConfig(moduleName, moduleFolder, moduleConfigComponent);
                if (moduleTemplatesConfig != null)
                {
                    filesCreated.Add(JsonSerialize(moduleTemplatesConfig.Url));
                }
            }
            filesCreated.AddRange(PublishJsonData(ReadTaxonomiesData(), coreConfigComponent, "taxonomies", _configStructureGroup));
            
            //Publish the boostrap list, this is used by the web application to load in all other configuration files
            PublishBootstrapJson(filesCreated, coreConfigComponent, _configStructureGroup, "config-", BuildAdditionalData());

            StringBuilder publishedFiles = new StringBuilder();
            foreach (string file in filesCreated)
            {
                if (!String.IsNullOrEmpty(file))
                {
                    publishedFiles.AppendCommaSeparated(file);
                    Logger.Info("Published " + file);
                }
            }

            // Update JSON Summary Report in Output Item.
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

        private string PublishModuleConfig(string moduleName, Component moduleConfigComponent)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            ItemFields fields = new ItemFields(moduleConfigComponent.Content, moduleConfigComponent.Schema);
            foreach (Component configComp in fields.GetComponentValues("furtherConfiguration"))
            {
                data = MergeData(data, ReadComponentData(configComp));
                switch (configComp.Title)
                {
                    case LocalizationConfigComponentTitle:
                        _localizationConfigurationComponent = configComp;
                        break;

                    case EnvironmentConfigComponentTitle:
                        if (Session.ApiVersion.StartsWith("8."))
                        {
                            string cmWebsiteUrl = TopologyManager.GetCmWebsiteUrl();
                            string cmsUrl;
                            if (data.TryGetValue(CmsUrlKey, out cmsUrl) && !string.IsNullOrWhiteSpace(cmsUrl))
                            {
                                Logger.Warning(
                                    string.Format("Overriding '{0}' specified in '{1}' Component ('{2}') with CM Website URL obtained from Topology Manager: '{3}'", 
                                        CmsUrlKey, EnvironmentConfigComponentTitle, cmsUrl, cmWebsiteUrl)
                                    );
                            }
                            else
                            {
                                Logger.Info(string.Format("Setting '{0}' to CM Website URL obtained from Topology Manager: '{1}'", CmsUrlKey, cmWebsiteUrl));
                            }
                            data[CmsUrlKey] = cmWebsiteUrl;
                        }
                        break;

                    case SearchConfigComponentTitle:
                        string cdEnvironmentPurpose = Utility.GetCdEnvironmentPurpose(Engine.PublishingContext);
                        if (!string.IsNullOrEmpty(cdEnvironmentPurpose))
                        {
                            string searchQueryUrl = TopologyManager.GetSearchQueryUrl((Publication)configComp.ContextRepository, cdEnvironmentPurpose);
                            if (!string.IsNullOrEmpty(searchQueryUrl))
                            {
                                string legacyConfigKey = Utility.IsXpmEnabled(Engine.PublishingContext) ? StagingSearchIndexKey : LiveSearchIndexKey;
                                Logger.Info(string.Format("Setting '{0}' and '{1}' to Search Query URL obtained from Topology Manager: '{2}'", 
                                    SearchQueryUrlKey, legacyConfigKey, searchQueryUrl));
                                data[legacyConfigKey] = searchQueryUrl;
                                data[SearchQueryUrlKey] = searchQueryUrl;
                            }
                            else
                            {
                                Logger.Warning(string.Format("No Search Query URL defined in Topology Manager for Publication '{0}' and CD Environment Purpose '{1}'.", 
                                    configComp.ContextRepository.Id, cdEnvironmentPurpose));
                            }
                        }
                        break;

                }
            }
            return PublishJsonData(data, moduleConfigComponent, moduleName, "config", _configStructureGroup);
        }


        private List<string> BuildAdditionalData()
        {
            if (_localizationConfigurationComponent == null)
            {
                Logger.Warning("Could not find 'Localization Configuration' component, cannot publish language data");
            }
            IEnumerable<PublicationDetails> sitePubs = LoadSitePublications(GetPublication());
            bool isMaster = sitePubs.Where(p => p.Id == GetPublication().Id.ItemId.ToString(CultureInfo.InvariantCulture)).FirstOrDefault().IsMaster;
            List<string> additionalData = new List<string>
                {
                    String.Format("\"defaultLocalization\":{0}", JsonEncode(isMaster)),
                    String.Format("\"staging\":{0}", JsonEncode(IsPublishingToStaging())),
                    String.Format("\"mediaRoot\":{0}", JsonEncode(GetPublication().MultimediaUrl)),
                    String.Format("\"siteLocalizations\":{0}", JsonEncode(sitePubs))
                };
            return additionalData;
        }

        private Publication GetMasterPublication(Publication contextPublication)
        {
            string siteId = GetSiteIdFromPublication(contextPublication);
            List<Publication> validParents = new List<Publication>();
            if (siteId != null && siteId!="multisite-master")
            {
                foreach (Repository item in contextPublication.Parents)
                {
                    Publication parent = (Publication)item;
                    if (IsCandidateMaster(parent, siteId))
                    {
                        validParents.Add(parent);
                    }
                }
            }
            if (validParents.Count > 1)
            {
                Logger.Error(String.Format("Publication {0} has more than one parent with the same (or empty) siteId {1}. Cannot determine site grouping, so picking the first parent: {2}.", contextPublication.Title, siteId, validParents[0].Title));
            }
            return validParents.Count==0 ? contextPublication : GetMasterPublication(validParents[0]);
        }

        private bool IsCandidateMaster(Publication pub, string childId)
        {
            //A publication is a valid master if:
            //a) Its siteId is "multisite-master" or
            //b) Its siteId matches the passed (child) siteId
            string siteId = GetSiteIdFromPublication(pub);
            return siteId == "multisite-master" || childId == siteId;
        }


        private IEnumerable<PublicationDetails> LoadSitePublications(Publication contextPublication)
        {
            string siteId = GetSiteIdFromPublication(contextPublication);
            Publication master = GetMasterPublication(contextPublication);
            Logger.Debug(String.Format("Master publication is : {0}, siteId is {1}", master.Title, siteId));
            List<PublicationDetails> pubs = new List<PublicationDetails>();
            bool masterAdded = false;
            if (GetSiteIdFromPublication(master) == siteId)
            {
                masterAdded = IsMasterWebPublication(master);
                pubs.Add(GetPublicationDetails(master, masterAdded));
            }
            if (siteId!=null)
            {
                pubs.AddRange(GetChildPublicationDetails(master, siteId, masterAdded));
            }
            //It is possible that no publication has been set explicitly as the master
            //in which case we set the context publication as the master
            if (!pubs.Any(p => p.IsMaster))
            {
                string currentPubId = GetPublication().Id.ItemId.ToString(CultureInfo.InvariantCulture);
                foreach (PublicationDetails pub in pubs)
                {
                    if (pub.Id==currentPubId)
                    {
                        pub.IsMaster = true;
                    }
                }
            }
            return pubs;
        }

        private PublicationDetails GetPublicationDetails(Publication pub, bool isMaster = false)
        {
            PublicationDetails pubData = new PublicationDetails { Id = pub.Id.ItemId.ToString(CultureInfo.InvariantCulture), Path = pub.PublicationUrl, IsMaster = isMaster};
            if (_localizationConfigurationComponent != null)
            {
                TcmUri localUri = new TcmUri(_localizationConfigurationComponent.Id.ItemId,ItemType.Component,pub.Id.ItemId);
                Component locComp = (Component)Engine.GetObject(localUri);
                if (locComp != null)
                {
                    ItemFields fields = new ItemFields(locComp.Content, locComp.Schema);
                    foreach (ItemFields field in fields.GetEmbeddedFields("settings"))
                    {
                        if (field.GetTextValue("name") == "language")
                        {
                            pubData.Language = field.GetTextValue("value");
                            break;
                        }
                    }
                }
            }
            return pubData;
        }

        private IEnumerable<PublicationDetails> GetChildPublicationDetails(Publication master, string siteId, bool masterAdded)
        {
            List<PublicationDetails> pubs = new List<PublicationDetails>();
            UsingItemsFilter filter = new UsingItemsFilter(Engine.GetSession()) { ItemTypes = new List<ItemType> { ItemType.Publication } };
            foreach (XmlElement item in master.GetListUsingItems(filter).ChildNodes)
            {
                string id = item.GetAttribute("ID");
                Publication child = (Publication)Engine.GetObject(id);
                string childSiteId = GetSiteIdFromPublication(child);
                if (childSiteId == siteId)
                {
                    Logger.Debug(String.Format("Found valid descendent {0} with site ID {1} ", child.Title, childSiteId));
                    bool isMaster = !masterAdded && IsMasterWebPublication(child);
                    pubs.Add(GetPublicationDetails(child, isMaster));
                    masterAdded = masterAdded || isMaster;
                }
                else
                {
                    Logger.Debug(String.Format("Descendent {0} has invalid site ID {1} - ignoring ",child.Title,childSiteId));
                }
            }
            return pubs;
        }

        private string GetSiteIdFromPublication(Publication startPublication)
        {
            if (startPublication.Metadata!=null)
            {
                ItemFields meta = new ItemFields(startPublication.Metadata, startPublication.MetadataSchema);
                return meta.GetTextValue("siteId");
            }
            return null;
        }


        private Dictionary<string, List<string>> ReadTaxonomiesData()
        {
            //Generate a list of taxonomy + id
            Dictionary<string, List<string>> res = new Dictionary<string, List<string>>();
            List<string> settings = new List<string>();
            TaxonomiesFilter taxFilter = new TaxonomiesFilter(Engine.GetSession()) { BaseColumns = ListBaseColumns.Extended };
            foreach (XmlElement item in GetPublication().GetListTaxonomies(taxFilter).ChildNodes)
            {
                string id = item.GetAttribute("ID");
                Category taxonomy = (Category) Engine.GetObject(id);
                settings.Add(String.Format("{0}:{1}", JsonEncode(Utility.GetKeyFromTaxonomy(taxonomy)), JsonEncode(taxonomy.Id.ItemId)));
            }
            res.Add("core." + TaxonomiesConfigName, settings);
            return res;
        }


        private Folder GetModuleFolder(Component moduleConfigComponent)
        {
            IList<OrganizationalItem> moduleConfigAncestors = moduleConfigComponent.OrganizationalItem.GetAncestors().ToList();
            moduleConfigAncestors.Insert(0, moduleConfigComponent.OrganizationalItem);
            if (moduleConfigAncestors.Count < 3)
            {
                throw new ApplicationException(
                    String.Format("Unable to determine Module Folder for Module Configuration Component '{0}': too few parent Folders.", moduleConfigComponent.WebDavUrl)
                    );
            }

            // Module folder is always third level (under Root Folder and Modules folder).
            return (Folder) moduleConfigAncestors[moduleConfigAncestors.Count - 3];
        }

        private Binary PublishModuleSchemasConfig(string moduleName, Folder moduleFolder, Component moduleConfigComponent)
        {
            OrganizationalItemItemsFilter moduleSchemasFilter = new OrganizationalItemItemsFilter(Engine.GetSession())
            {
                ItemTypes =  new [] { ItemType.Schema },
                Recursive = true
            };

            Schema[] moduleSchemas = moduleFolder.GetItems(moduleSchemasFilter).Cast<Schema>().Where(s => s.Purpose == SchemaPurpose.Component).ToArray();
            if (!moduleSchemas.Any())
            {
                return null;
            }

            IDictionary <string, int> moduleSchemasConfig = new Dictionary<string, int>();
            foreach (Schema moduleSchema in moduleSchemas)
            {
                string schemaKey = Utility.GetKeyFromSchema(moduleSchema);
                int sameKeyAsSchema;
                if (moduleSchemasConfig.TryGetValue(schemaKey, out sameKeyAsSchema))
                {
                    Logger.Warning(string.Format("{0} has same key ('{1}') as Schema '{2}'; supressing from output.", moduleSchema, schemaKey, sameKeyAsSchema));
                    continue;
                }
                moduleSchemasConfig.Add(schemaKey, moduleSchema.Id.ItemId);
            }

            return AddJsonBinary(
                moduleSchemasConfig,
                relatedComponent: moduleConfigComponent,
                structureGroup: _configStructureGroup,
                name: string.Format("{0}.{1}", moduleName, SchemasConfigName),
                variantId: "schemas"
                );
        }

        private Binary PublishModuleTemplatesConfig(string moduleName, Folder moduleFolder, Component moduleConfigComponent)
        {
            OrganizationalItemItemsFilter moduleTemplatesFilter = new OrganizationalItemItemsFilter(Engine.GetSession())
            {
                ItemTypes = new[] { ItemType.ComponentTemplate },
                Recursive = true
            };

            ComponentTemplate[] moduleComponentTemplates = moduleFolder.GetItems(moduleTemplatesFilter).Cast<ComponentTemplate>().Where(ct => ct.IsRepositoryPublishable).ToArray();
            if (!moduleComponentTemplates.Any())
            {
                return null;
            }

            IDictionary<string, int> moduleTemplatesConfig = new Dictionary<string, int>();
            foreach (ComponentTemplate moduleTemplate in moduleComponentTemplates)
            {
                string templateKey = Utility.GetKeyFromTemplate(moduleTemplate);
                int sameKeyAsTemplate;
                if (moduleTemplatesConfig.TryGetValue(templateKey, out sameKeyAsTemplate))
                {
                    Logger.Warning(string.Format("{0} has same key ('{1}') as CT '{2}'; supressing from output.", moduleTemplate, templateKey, sameKeyAsTemplate));
                    continue;
                }
                moduleTemplatesConfig.Add(templateKey, moduleTemplate.Id.ItemId);
            }

            return AddJsonBinary(
                moduleTemplatesConfig,
                relatedComponent: moduleConfigComponent,
                structureGroup: _configStructureGroup,
                name: string.Format("{0}.{1}", moduleName, TemplateConfigName),
                variantId: "templates"
                );
        }
    }

    internal class PublicationDetails
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Language { get; set; }
        public bool IsMaster { get; set; }
    }
}
