
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
            /// 开发环境指向 Vite 开发服务器（/api 代理到后端 8082）；
            /// 发布时改为实际部署地址，如 https://your-domain.com
            /// </summary>
            public const string ServerBaseUrl = "http://localhost:3007";
        }
    }
}
