﻿using Sdl.Web.Tridion.Common;
using System.Text;
using System.Text.RegularExpressions;
using Tridion.ContentManager.CommunicationManagement;
using Tridion.ContentManager.Templating;
using Tridion.ContentManager.Templating.Assembly;
using ComponentPresentation = Tridion.ContentManager.CommunicationManagement.ComponentPresentation;

namespace Sdl.Web.Tridion.Templates.Legacy
{
    /// <summary>
    /// Renders the component presentations to the package output. Useful when there is no page layout for publishing data
    /// </summary>
    [TcmTemplateTitle("Render Component Presentations")]
    public class RenderComponentPresentations : TemplateBase
    {
        public override void Transform(Engine engine, Package package)
        {
            Initialize(engine, package);
            Page page = GetPage();
            StringBuilder output = new StringBuilder();
            if (page != null)
            {
                foreach (ComponentPresentation cp in page.ComponentPresentations)
                {
                    output.AppendLine(RemoveTcdl(engine.RenderComponentPresentation(cp.Component.Id, cp.ComponentTemplate.Id)));
                }
            }
            package.PushItem(Package.OutputName, package.CreateStringItem(ContentType.Text, output.ToString()));
        }

        private static string RemoveTcdl(string p)
        {
            p = Regex.Replace(p, "<tcdl:ComponentPresentation[^>]+>", "");
            return p.Replace("</tcdl:ComponentPresentation>", "");
        }
    }
}
