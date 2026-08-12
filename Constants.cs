
namespace SideDesk
{
    public static class Constants
    {
        public static class Generic
        {
            public const string TASKNAME = "SideDeskStartup";
        }

        public static class URLs
        {
            public const string IPIFY = "https://api.ipify.org";

            /// <summary>
            /// 后端 API + 管理前端统一基址（发布时只需改这一处）。
            /// 开发环境可改回 Vite 开发服务器 http://localhost:3007（/api 代理到后端 8082）；
            /// 生产环境前后端均部署在 /console 子路径下
            /// </summary>
            public const string ServerBaseUrl = "https://api.sidedesk.bigjing.com/console";

            /// <summary>
            /// 前端站点基址（登录/分享/市场/编辑器等前端路由，不含 "#"）。
            /// 前后端同基址（生产 /console 子路径，开发环境 Vite 根路径），保留别名以便语义区分。
            /// </summary>
            public static string FrontendBaseUrl => ServerBaseUrl;

            /// <summary>ServerBaseUrl 的 origin 部分（scheme+host+port，不含路径），供 scheme 注册/CORS 等 origin 匹配场景</summary>
            public static string ServerBaseOrigin =>
                new System.Uri(ServerBaseUrl).GetLeftPart(System.UriPartial.Authority);
        }
    }
}
