import type { Code, Localized, ReplyBlock } from '../types'
import { buildPrompt, type PromptChoice } from './install-prompt'

// The install prompt and the agent's answer, shared by every install tutorial: whichever agent the
// reader uses, it gets the same instructions. The prompt is the one the prompt window builds for a
// Windows PC with SQLite, so the animation types exactly what a reader would copy. Captions and app
// wording stay with each tutorial.

const script = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1'
const guide = 'https://raw.githubusercontent.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/main/skills/deploy-server/SKILL.md'
const pc: PromptChoice = { target: 'pc', os: 'windows', db: 'sqlite', engine: 'mysql', dbUser: '', dbPassword: '', agent: 'pc', host: '', sshUser: '', runtime: 'native', docker: 'installed' }
const codes: Code[] = ['en', 'zh-CN', 'zh-TW', 'ko']

export const prompt = Object.fromEntries(codes.map(code => [code, buildPrompt(pc, code)])) as Localized

/** The agent's reply, starting `start` milliseconds into the step. */
export const reply = (start: number): ReplyBlock[] => [
  { at: start + 0, kind: 'text', text: {
    en: 'Sure. I will read the deployment guide first, then install the server with the project\'s own script.',
    'zh-CN': '好的。我先阅读部署指南，再用项目自带的脚本安装服务器。',
    'zh-TW': '好的。我先閱讀部署指南，再用專案內建的腳本安裝伺服器。',
    ko: '네. 먼저 배포 가이드를 읽고, 프로젝트의 설치 스크립트로 서버를 설치하겠습니다.',
  } },
  { at: start + 1000, kind: 'tool', title: '运行命令', dur: 1600, summary: { en: 'Read the deployment guide', 'zh-CN': '阅读部署指南', 'zh-TW': '閱讀部署指南', ko: '배포 가이드 읽기' },
    lines: [{ cmd: `Invoke-RestMethod ${guide}` }, { out: '# Deploy a Bubble Fighter Fantasy server' }, { out: '...' }] },
  { at: start + 2900, kind: 'tool', title: '运行命令', dur: 6600, summary: { en: 'Run the install script', 'zh-CN': '运行安装脚本', 'zh-TW': '執行安裝腳本', ko: '설치 스크립트 실행' },
    lines: [
      { cmd: `& ([scriptblock]::Create((irm ${script}))) -NoPrompt -Start` },
      { out: '==> Fetching bubblefighter-fantasy-win-x64.zip (v0.4.0)' },
      { out: '==> Checking the download' },
      { out: '==> Installing the ASP.NET Core Runtime 10 (x64) into C:\\BubbleFighterFantasy\\dotnet' },
      { out: '==> Unpacking into C:\\BubbleFighterFantasy\\releases\\v0.4.0' },
      { out: '==> Starting the server in its own window' },
      { out: 'Bubble Fighter Fantasy v0.4.0 (x64) is installed in C:\\BubbleFighterFantasy.' },
    ] },
  { at: start + 9800, kind: 'tool', title: '运行命令', dur: 1500, summary: { en: 'Check the server', 'zh-CN': '检查服务器', 'zh-TW': '檢查伺服器', ko: '서버 확인' },
    lines: [{ cmd: 'Invoke-RestMethod http://127.0.0.1:7300/health' }, { out: 'status: ok' }] },
  { at: start + 11600, kind: 'md', dur: 3600, md: {
    en: `### Bubble Fighter Fantasy is installed
- **Version**: v0.4.0, \`win-x64\` package (checksum verified by the script)
- **Folder**: \`C:\\BubbleFighterFantasy\`; the ASP.NET Core Runtime 10 was installed into its \`dotnet\` folder
- **Start**: double-click \`C:\\BubbleFighterFantasy\\bff.cmd\`
- **Stop**: close the server's window
- **Next**: the setup page [http://127.0.0.1:7300/](http://127.0.0.1:7300/) is open in your browser. The setup wizard is yours to finish: language, server name, client versions, client data, database (choose **SQLite**) and the administrator account.
The server does not include the game client or client data; import your own data in the wizard or later.`,
    'zh-CN': `### Bubble Fighter Fantasy 已安装
- **版本**：v0.4.0，\`win-x64\` 安装包（脚本已校验 SHA256）
- **位置**：\`C:\\BubbleFighterFantasy\`；ASP.NET Core Runtime 10 已安装到其中的 \`dotnet\` 文件夹
- **启动**：双击 \`C:\\BubbleFighterFantasy\\bff.cmd\`
- **停止**：关闭服务器窗口
- **下一步**：设置页面 [http://127.0.0.1:7300/](http://127.0.0.1:7300/) 已在浏览器中打开。设置向导由你来完成：语言、服务器名称、客户端版本、客户端数据、数据库（选择 **SQLite**）和管理员账号。
服务器不包含游戏客户端或客户端数据；可以在向导中或之后导入你自己的数据。`,
    'zh-TW': `### Bubble Fighter Fantasy 已安裝
- **版本**：v0.4.0，\`win-x64\` 安裝套件（腳本已校驗 SHA256）
- **位置**：\`C:\\BubbleFighterFantasy\`；ASP.NET Core Runtime 10 已安裝到其中的 \`dotnet\` 資料夾
- **啟動**：按兩下 \`C:\\BubbleFighterFantasy\\bff.cmd\`
- **停止**：關閉伺服器視窗
- **下一步**：設定頁面 [http://127.0.0.1:7300/](http://127.0.0.1:7300/) 已在瀏覽器中開啟。設定精靈由你來完成：語言、伺服器名稱、用戶端版本、用戶端資料、資料庫（選擇 **SQLite**）和管理員帳號。
伺服器不包含遊戲用戶端或用戶端資料；可以在精靈中或之後匯入你自己的資料。`,
    ko: `### Bubble Fighter Fantasy 설치 완료
- **버전**: v0.4.0, \`win-x64\` 패키지(스크립트가 체크섬 확인)
- **폴더**: \`C:\\BubbleFighterFantasy\`, ASP.NET Core Runtime 10은 그 안의 \`dotnet\` 폴더에 설치됨
- **시작**: \`C:\\BubbleFighterFantasy\\bff.cmd\`를 두 번 클릭
- **중지**: 서버 창을 닫기
- **다음**: 설정 페이지 [http://127.0.0.1:7300/](http://127.0.0.1:7300/)이 브라우저에 열려 있습니다. 설정 마법사는 직접 마치세요: 언어, 서버 이름, 클라이언트 버전, 클라이언트 데이터, 데이터베이스(**SQLite** 선택), 관리자 계정.
서버에는 게임 클라이언트나 클라이언트 데이터가 포함되어 있지 않습니다. 마법사에서 또는 나중에 직접 가져오세요.`,
  } },
]
