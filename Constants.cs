
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
            /// 产品主页（根域名）基址：域名由构建参数选择（见 SideDesk.csproj 的 ApiRegion）：
            /// dotnet build -p:ApiRegion=zh → bigjing（默认）；-p:ApiRegion=en → desktxp。
            /// 其它根域名用途默认从本地址派生，勿另维护硬编码域名
            /// （例外：中文版更新源改用独立 CDN，见下方 VelopackUpdateUrl）。
            /// </summary>
#if API_REGION_EN
            public const string HomePageBaseUrl = "https://sidedesk.desktxp.com";
#else
            public const string HomePageBaseUrl = "https://sidedesk.bigjing.com";
#endif

            /// <summary>
            /// Velopack 自动更新源（自建静态文件服务器，存放 vpk pack 产出的 RELEASES / packages）。
            /// 发布命令：vpk pack --packId SideDesk --outputDir ...，将产物上传至此地址。
            /// 中文版使用独立 CDN 域名（cdn.bigjing.com/updates/sidedesk-cn），不再从 HomePageBaseUrl 派生；
            /// 国际版仍从 HomePageBaseUrl 派生（sidedesk.desktxp.com/updates）。
            /// </summary>
#if API_REGION_EN
            public const string VelopackUpdateUrl = HomePageBaseUrl + "/updates";
#else
            public const string VelopackUpdateUrl = "https://cdn.bigjing.com/updates/sidedesk-cn";
#endif

            /// <summary>
            /// 后端 API + 管理前端统一基址（发布时只需改这一处）。
            /// 开发环境可改回 Vite 开发服务器 http://localhost:3007（/api 代理到后端 8082）；
            /// 生产环境前后端均部署在 /console 子路径下。
            /// origin 匹配场景（scheme 注册/CORS）需要 scheme://host[:port] 时，
            /// 用 new Uri(ServerBaseUrl).GetLeftPart(UriPartial.Authority) 从本地址派生，勿另维护硬编码常量。
            /// 域名由构建参数选择（见 SideDesk.csproj 的 ApiRegion）：
            /// dotnet build -p:ApiRegion=zh → bigjing（默认）；-p:ApiRegion=en → desktxp。
            /// </summary>
#if API_REGION_EN
            public const string ServerBaseUrl = "https://api.sidedesk.desktxp.com/console";
#else
            // 正式环境（生产域名，前后端均部署在 /console 子路径下）；
            // 本地调试时改回 Vite dev server http://localhost:3007（/api 代理到后端 8082，WS 回退直连 127.0.0.1:19190）
            public const string ServerBaseUrl = "https://api.sidedesk.bigjing.com/console";
#endif

            /// <summary>
            /// 前端站点基址（登录/分享/市场/编辑器等前端路由，不含 "#"）。
            /// 前后端同基址（生产 /console 子路径，开发环境 Vite 根路径），保留别名以便语义区分。
            /// </summary>
            public static string FrontendBaseUrl => ServerBaseUrl;

            /// <summary>是否本地/测试环境（ServerBaseUrl 指向本机）；仅测试环境展示的调试提示可用此开关控制</summary>
            public static bool IsDev =>
                ServerBaseUrl.Contains("localhost", System.StringComparison.OrdinalIgnoreCase) ||
                ServerBaseUrl.Contains("127.0.0.1");

            /// <summary>
            /// OAuth2「系统浏览器登录」的客户端 ID（授权码 + PKCE 流程）。
            /// 须与后端 oauth.clientIds 白名单一致（见 sidedesk-admin-backend 的 config/dev.yml、config/prod.yml）。
            /// </summary>
            public const string OAuthClientId = "sidedesk-desktop";
        }
    }
}
