
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
            /// Velopack 自动更新源（自建静态文件服务器，存放 vpk pack 产出的 RELEASES / packages）。
            /// 发布命令：vpk pack --packId SideDesk --outputDir ...，将产物上传至此地址。
            /// </summary>
            public const string VelopackUpdateUrl = "https://sidedesk.desktxp.com/updates";

            /// <summary>
            /// 后端 API + 管理前端统一基址（发布时只需改这一处）。
            /// 开发环境可改回 Vite 开发服务器 http://localhost:3007（/api 代理到后端 8082）；
            /// 生产环境前后端均部署在 /console 子路径下。
            /// origin 匹配场景（scheme 注册/CORS）需要 scheme://host[:port] 时，
            /// 用 new Uri(ServerBaseUrl).GetLeftPart(UriPartial.Authority) 从本地址派生，勿另维护硬编码常量。
            /// </summary>
            // 当前指向：线上生产环境（前后端均部署在 /console 子路径下）；本地联调可改为 http://localhost:3007
            public const string ServerBaseUrl = "https://api.sidedesk.bigjing.com/console";

            /// <summary>
            /// 前端站点基址（登录/分享/市场/编辑器等前端路由，不含 "#"）。
            /// 前后端同基址（生产 /console 子路径，开发环境 Vite 根路径），保留别名以便语义区分。
            /// </summary>
            public static string FrontendBaseUrl => ServerBaseUrl;

            /// <summary>是否本地/测试环境（ServerBaseUrl 指向本机）；仅测试环境展示的调试提示可用此开关控制</summary>
            public static bool IsDev =>
                ServerBaseUrl.Contains("localhost", System.StringComparison.OrdinalIgnoreCase) ||
                ServerBaseUrl.Contains("127.0.0.1");
        }
    }
}
