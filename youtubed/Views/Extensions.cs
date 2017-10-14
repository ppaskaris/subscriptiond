using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed
{
    public static class Extensions
    {
        public static String ActiveClass(this IUrlHelper @this, string controller, string action)
        {
            string controllerName = @this.ActionContext.RouteData.Values["controller"].ToString();
            string actionName = @this.ActionContext.RouteData.Values["action"].ToString();
            bool isActive =
                (controller == null || controller.Equals(controllerName, StringComparison.OrdinalIgnoreCase)) &&
                (action == null || action.Equals(actionName, StringComparison.OrdinalIgnoreCase));
            return isActive ? "active" : null;
        }
    }
}
