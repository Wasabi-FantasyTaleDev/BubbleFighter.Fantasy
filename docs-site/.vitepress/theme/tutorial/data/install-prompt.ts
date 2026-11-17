import type { Code } from '../types'

// Builds the install prompt from the reader's answers in the prompt window: where the server goes,
// its operating system, the database and where the agent runs. Whatever the answers, the agent runs
// the project's own install script (deploy/install.ps1 or deploy/install.sh) and only does what the
// script does not: installing a database natively, SSH, the tunnel and the summary. Every fact in
// these texts (script options, the database defaults, port 7300) matches the installation guide and
// skills/deploy-server/SKILL.md; change them together.

const project = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy'
const skill = `${project}/blob/main/skills/deploy-server/SKILL.md`
const release = `${project}/releases/latest/download`
const scriptPs = `${release}/install.ps1`
const scriptSh = `${release}/install.sh`

export type Place = 'server' | 'pc'
export type Engine = 'mysql' | 'mariadb'

/** The reader's answers. `agent` only matters for a server; on a PC the agent runs on it. */
export interface PromptChoice {
  target: Place
  os: 'windows' | 'linux'
  db: 'install' | 'existing' | 'sqlite'
  engine: Engine
  dbUser: string
  dbPassword: string
  agent: Place
  host: string
  sshUser: string
  /** Linux servers only: run natively as a systemd service or with Docker Compose. */
  runtime: 'native' | 'docker'
  /** With Docker: whether Docker is already on the server or the agent installs it. */
  docker: 'installed' | 'install'
}

/** The versions the bundled Docker setup uses; both are long-term-support releases. */
export const engines: Record<Engine, string> = { mysql: 'MySQL 8.4', mariadb: 'MariaDB 11.4' }

interface Ctx {
  win: boolean
  pc: boolean
  remote: boolean
  os: string
  ssh: string
  known: boolean
  engine: string
  user: string
  password: string
  db: PromptChoice['db']
  docker: boolean
  dockerInstall: boolean
  /** Docker with the bundled database container: the installer creates its account. */
  dockerDb: boolean
}

const q = (s: string) => '`' + s + '`'

interface Words {
  intro(c: Ctx): string
  ssh(c: Ctx): string
  guide: string
  winScript(c: Ctx, cmd: string): string
  openOnServer: string
  tunnel(c: Ctx): string
  linuxInstall(c: Ctx, cmd: string): string
  dockerSetup(): string
  dockerRun(c: Ctx, cmd: string): string
  openPc: string
  install(c: Ctx): string
  existing(c: Ctx): string
  sqlite: string
  rules(c: Ctx): string
  summary(c: Ctx): string
}

const en: Words = {
  intro: c => c.remote
    ? `Install the Bubble Fighter Fantasy server on my ${c.os} server. You are running on my own computer, not on the server, so carry out every step on the server over SSH.`
    : `Install the Bubble Fighter Fantasy server on this ${c.os} ${c.pc ? 'PC' : 'server'}.`,
  ssh: c => `${c.known ? 'Connect' : 'Ask me for the server address and SSH user first, then connect'} with ${q(`ssh ${c.ssh}`)} and run every command below on the server, never on this computer.${c.win ? ' A Windows server needs OpenSSH Server; its commands run in PowerShell.' : ''} Use my existing SSH key. If the connection asks for a password or fails, stop and tell me how to set up key sign-in; do not ask me to type passwords into this chat.`,
  guide: `Project: ${project}. Before you start, read the deployment guide ${skill} and follow it: it explains every option\n   of the install scripts and how to fix common problems.`,
  winScript: (c, cmd) => `Install the server with the project's own script in ${c.pc ? 'PowerShell' : 'an administrator PowerShell (-Service needs one; if your shell is not\n   elevated, stop and ask me to run the command as administrator)'}. It detects the processor, downloads the matching package and\n   SHA256SUMS, verifies the checksum, installs the ASP.NET Core Runtime 10 when it is missing and installs into C:\\BubbleFighterFantasy:\n   ${cmd}\n   ${c.pc
    ? 'It starts the server in its own window, waits until http://127.0.0.1:7300/health answers "ok" and opens the setup page in my browser.'
    : 'It registers the scheduled task BubbleFighterFantasy (runs as SYSTEM, starts with Windows, keeps running after you disconnect),\n   starts it and waits until http://127.0.0.1:7300/health answers "ok".'}\n   If the script stops with an error, read it, fix the cause with the deployment guide and run the same command again.`,
  openOnServer: 'Open http://127.0.0.1:7300/ in the browser on this server for the first-run setup.',
  tunnel: c => `Keep port 7300 (the setup page and WebUI) closed to the internet. Tell me to open it from my computer through an\n   SSH tunnel: ${q(`ssh -L 7300:127.0.0.1:7300 ${c.ssh}`)}, then http://127.0.0.1:7300/ in my browser.`,
  linuxInstall: (c, cmd) => `Install the server with the project's own script. It detects the processor, downloads the matching package and\n   SHA256SUMS, verifies the checksum and installs the ASP.NET Core Runtime 10 when it is missing (install curl first if needed):\n   ${cmd}\n   ${c.pc
    ? `It installs into ~/bubblefighter-fantasy. Then start ~/bubblefighter-fantasy/bff in the background (for example with nohup,\n   logging to ~/bubblefighter-fantasy/bff.log)`
    : 'As root it installs into /opt/bubblefighter-fantasy and creates and starts the bubblefighter-fantasy systemd service.\n   Then'} wait until ${q('curl -s http://127.0.0.1:7300/health')} answers "ok".`,
  openPc: 'Open http://127.0.0.1:7300/ in my browser for the first-run setup.',
  install: c => `Install ${c.engine} ${c.win ? 'with winget or the official installer' : "from the distribution's packages or the vendor's official repository"}, make it start with the\n   system and listen on 127.0.0.1 only. Create the database bf_game_db (utf8mb4) and the user ${q(c.user)} ${c.password ? `with the password ${q(c.password)}` : 'with a strong random\n   password you generate'}, with all privileges on bf_game_db only. If the setup asks for a root password, generate a different strong one.`,
  dockerSetup: () => `Docker Engine and the Docker Compose plugin are already installed: check that ${q('docker compose version')} works and that the\n   docker service starts with the system. Do not reinstall Docker.`,
  dockerRun: (c, cmd) => `Install and start the server in Docker with the project's own script.${c.dockerInstall ? " With --install-docker it first installs Docker\n   Engine and the Compose plugin with Docker's official script and enables the docker service." : ''} It downloads and verifies the package, builds a\n   local image and starts it with Docker Compose${c.dockerDb ? ', together with a MariaDB 11.4 database container that listens on\n   127.0.0.1:3306 only, with the database bf_game_db, the user bff and a random password' : ''}:\n   ${cmd}\n   As root it keeps the stack in /opt/bubblefighter-fantasy/docker, and Compose restarts the containers with the system.\n   Then wait until ${q('curl -s http://127.0.0.1:7300/health')} answers "ok".`,
  existing: c => `MySQL or MariaDB is already installed: do not reinstall or reconfigure it. Check that it is running and that the user\n   ${q(c.user)} can sign in on 127.0.0.1:3306${c.password ? ` with the password ${q(c.password)}` : ' (ask me for the password)'}. The setup page creates the database bf_game_db if it does not exist.`,
  sqlite: 'Do not install a database server; I will choose SQLite on the setup page.',
  rules: c => 'Do not go through, complete or skip the setup wizard on the WebUI yourself (no API calls, no state or config files, no\n   run --profile): I finish it in the browser unless I ask you to. ' + (c.pc
    ? 'Do not change firewall, antivirus or system security settings, and do not touch unrelated files or programs.'
    : 'Do not change firewall, antivirus or SSH settings, do not open ports yourself, and do not touch unrelated files or services.'),
  summary: c => `Finish with a short Markdown summary: the version, the install folder, how to start and stop the server${c.db === 'sqlite'
    ? ", a reminder to choose SQLite on the setup page's database step"
    : `, what to enter on the setup page's database step\n   (MySQL, host 127.0.0.1, port 3306, database bf_game_db, ${c.dockerDb ? 'user bff and the BFF_MYSQL_PASSWORD value from /opt/bubblefighter-fantasy/docker/.env' : `user ${c.user} and its password${c.db === 'install' ? ', plus any password you generated' : ''}`})`}${c.pc ? '' : ',\n   the ports to open for players in the firewall or cloud security group (the game port of each client version I select\n   on the setup page, for example 7100 for kr-1256 and 7110 for kr-812)'}, and what I should do next on the setup page.`,
}

const zhCN: Words = {
  intro: c => c.remote
    ? `请把 Bubble Fighter Fantasy 服务器安装到我的 ${c.os} 服务器上。你运行在我自己的电脑上，而不是服务器上，所以每一步都要通过 SSH 在服务器上完成。`
    : `请把 Bubble Fighter Fantasy 服务器安装到这台 ${c.os} ${c.pc ? '电脑' : '服务器'}上。`,
  ssh: c => `${c.known ? '' : '先向我询问服务器地址和 SSH 用户，然后'}用 ${q(`ssh ${c.ssh}`)} 连接，下面的每条命令都在服务器上运行，绝不在这台电脑上运行。${c.win ? 'Windows 服务器需要 OpenSSH Server，命令在 PowerShell 中运行。' : ''}使用我现有的 SSH 密钥；如果连接要求输入密码或失败，就停下来告诉我如何设置密钥登录，不要让我把密码输入到这个对话里。`,
  guide: `项目地址：${project}。开始前先阅读部署指南 ${skill} 并按它操作：它说明了安装脚本的每个选项\n   以及常见问题的解决方法。`,
  winScript: (c, cmd) => `用项目自带的脚本安装服务器，在${c.pc ? ' PowerShell ' : '管理员 PowerShell（-Service 需要管理员权限；如果你的命令行没有提升权限，\n   就停下来让我以管理员身份运行命令）'}中运行。它会检测处理器，下载对应的安装包和 SHA256SUMS 并校验，缺少 ASP.NET Core\n   Runtime 10 时会自动安装，并安装到 C:\\BubbleFighterFantasy：\n   ${cmd}\n   ${c.pc
    ? '它会在单独的窗口中启动服务器，等待 http://127.0.0.1:7300/health 返回 "ok"，并在我的浏览器中打开设置页面。'
    : '它会注册计划任务 BubbleFighterFantasy（以 SYSTEM 运行，随 Windows 启动，你断开连接后继续运行），\n   立即启动它并等待 http://127.0.0.1:7300/health 返回 "ok"。'}\n   如果脚本报错停止，先阅读错误，按部署指南排除原因，再运行同一条命令。`,
  openOnServer: '在这台服务器的浏览器中打开 http://127.0.0.1:7300/ 进行首次设置。',
  tunnel: c => `不要把 7300 端口（设置页面和 WebUI）开放到公网。告诉我从自己的电脑通过 SSH 隧道打开它：\n   ${q(`ssh -L 7300:127.0.0.1:7300 ${c.ssh}`)}，然后在浏览器打开 http://127.0.0.1:7300/。`,
  linuxInstall: (c, cmd) => `用项目自带的脚本安装服务器。它会检测处理器，下载对应的安装包和 SHA256SUMS 并校验，缺少 ASP.NET Core Runtime 10\n   时会自动安装（如果没有 curl，先安装 curl）：\n   ${cmd}\n   ${c.pc
    ? '它会安装到 ~/bubblefighter-fantasy。然后在后台启动 ~/bubblefighter-fantasy/bff（例如用 nohup，日志写到\n   ~/bubblefighter-fantasy/bff.log），'
    : '以 root 运行时它会安装到 /opt/bubblefighter-fantasy，并创建和启动 bubblefighter-fantasy systemd 服务。然后'}等待 ${q('curl -s http://127.0.0.1:7300/health')} 返回 "ok"。`,
  openPc: '在我的浏览器中打开 http://127.0.0.1:7300/ 进行首次设置。',
  install: c => `${c.win ? '用 winget 或官方安装程序' : '从发行版软件源或官方仓库'}安装 ${c.engine}，设置为开机启动，并只监听 127.0.0.1。创建数据库\n   bf_game_db（utf8mb4）和用户 ${q(c.user)}，${c.password ? `密码为 ${q(c.password)}` : '密码由你随机生成一个强密码'}，只授予 bf_game_db 的全部权限。如果安装需要 root 密码，另外生成一个强密码。`,
  dockerSetup: () => `已经安装了 Docker Engine 和 Docker Compose 插件：检查 ${q('docker compose version')} 能正常运行，并且 docker 服务\n   会开机启动。不要重新安装 Docker。`,
  dockerRun: (c, cmd) => `用项目自带的脚本在 Docker 中安装并启动服务器。${c.dockerInstall ? '加上 --install-docker 时，它会先用 Docker 官方脚本安装\n   Docker Engine 和 Compose 插件，并启用 docker 服务。' : ''}它会下载并校验安装包，构建本地镜像，并用 Docker Compose 启动${c.dockerDb ? '，\n   同时启动一个只监听 127.0.0.1:3306 的 MariaDB 11.4 数据库容器，包含数据库 bf_game_db、用户 bff 和随机密码' : ''}：\n   ${cmd}\n   以 root 运行时，容器配置保存在 /opt/bubblefighter-fantasy/docker，Compose 会让容器随系统启动。\n   然后等待 ${q('curl -s http://127.0.0.1:7300/health')} 返回 "ok"。`,
  existing: c => `已经安装了 MySQL 或 MariaDB：不要重新安装或修改它的配置。检查它正在运行，并且用户 ${q(c.user)}\n   能在 127.0.0.1:3306 登录${c.password ? `，密码为 ${q(c.password)}` : '（向我询问密码）'}。如果数据库 bf_game_db 不存在，设置页面会创建它。`,
  sqlite: '不要安装数据库服务器；我会在设置页面选择 SQLite。',
  rules: c => '不要自己操作、完成或跳过 WebUI 上的设置向导（不要调用 API、不要写状态或配置文件、不要用 run --profile）：\n   除非我要求，由我在浏览器中完成。' + (c.pc
    ? '不要修改防火墙、杀毒软件或系统安全设置，也不要改动无关的文件或程序。'
    : '不要修改防火墙、杀毒软件或 SSH 设置，不要自行开放端口，也不要改动无关的文件或服务。'),
  summary: c => `最后用简短的 Markdown 总结：版本、安装位置、如何启动和停止服务器${c.db === 'sqlite'
    ? '、提醒我在设置页面的数据库步骤选择 SQLite'
    : `、设置页面数据库步骤要填写的内容\n   （MySQL，主机 127.0.0.1，端口 3306，数据库 bf_game_db，${c.dockerDb ? '用户 bff，密码为 /opt/bubblefighter-fantasy/docker/.env 中 BFF_MYSQL_PASSWORD 的值' : `用户 ${c.user} 及其密码${c.db === 'install' ? '，以及你生成的密码' : ''}`}）`}${c.pc ? '' : '、\n   需要在防火墙或云安全组中为玩家开放的端口（我在设置页面选择的每个客户端版本的游戏端口，例如 kr-1256 是 7100，\n   kr-812 是 7110）'}，以及我在设置页面接下来该做什么。`,
}

const zhTW: Words = {
  intro: c => c.remote
    ? `請把 Bubble Fighter Fantasy 伺服器安裝到我的 ${c.os} 伺服器上。你執行在我自己的電腦上，而不是伺服器上，所以每一步都要透過 SSH 在伺服器上完成。`
    : `請把 Bubble Fighter Fantasy 伺服器安裝到這台 ${c.os} ${c.pc ? '電腦' : '伺服器'}上。`,
  ssh: c => `${c.known ? '' : '先向我詢問伺服器位址和 SSH 使用者，然後'}用 ${q(`ssh ${c.ssh}`)} 連線，下面的每個命令都在伺服器上執行，絕不在這台電腦上執行。${c.win ? 'Windows 伺服器需要 OpenSSH Server，命令在 PowerShell 中執行。' : ''}使用我現有的 SSH 金鑰；如果連線要求輸入密碼或失敗，就停下來告訴我如何設定金鑰登入，不要讓我把密碼輸入到這個對話裡。`,
  guide: `專案位址：${project}。開始前先閱讀部署指南 ${skill} 並依照它操作：它說明了安裝腳本的每個選項\n   以及常見問題的解決方法。`,
  winScript: (c, cmd) => `用專案內建的腳本安裝伺服器，在${c.pc ? ' PowerShell ' : '系統管理員 PowerShell（-Service 需要系統管理員權限；如果你的命令列沒有提升權限，\n   就停下來讓我以系統管理員身分執行命令）'}中執行。它會偵測處理器，下載對應的安裝套件和 SHA256SUMS 並校驗，缺少 ASP.NET Core\n   Runtime 10 時會自動安裝，並安裝到 C:\\BubbleFighterFantasy：\n   ${cmd}\n   ${c.pc
    ? '它會在獨立的視窗中啟動伺服器，等待 http://127.0.0.1:7300/health 回傳 "ok"，並在我的瀏覽器中開啟設定頁面。'
    : '它會註冊排程工作 BubbleFighterFantasy（以 SYSTEM 執行，隨 Windows 啟動，你中斷連線後繼續執行），\n   立即啟動它並等待 http://127.0.0.1:7300/health 回傳 "ok"。'}\n   如果腳本報錯停止，先閱讀錯誤，依照部署指南排除原因，再執行同一個命令。`,
  openOnServer: '在這台伺服器的瀏覽器中開啟 http://127.0.0.1:7300/ 進行首次設定。',
  tunnel: c => `不要把 7300 連接埠（設定頁面和 WebUI）開放到網際網路。告訴我從自己的電腦透過 SSH 通道開啟它：\n   ${q(`ssh -L 7300:127.0.0.1:7300 ${c.ssh}`)}，然後在瀏覽器開啟 http://127.0.0.1:7300/。`,
  linuxInstall: (c, cmd) => `用專案內建的腳本安裝伺服器。它會偵測處理器，下載對應的安裝套件和 SHA256SUMS 並校驗，缺少 ASP.NET Core Runtime 10\n   時會自動安裝（如果沒有 curl，先安裝 curl）：\n   ${cmd}\n   ${c.pc
    ? '它會安裝到 ~/bubblefighter-fantasy。然後在背景啟動 ~/bubblefighter-fantasy/bff（例如用 nohup，記錄寫到\n   ~/bubblefighter-fantasy/bff.log），'
    : '以 root 執行時它會安裝到 /opt/bubblefighter-fantasy，並建立和啟動 bubblefighter-fantasy systemd 服務。然後'}等待 ${q('curl -s http://127.0.0.1:7300/health')} 回傳 "ok"。`,
  openPc: '在我的瀏覽器中開啟 http://127.0.0.1:7300/ 進行首次設定。',
  install: c => `${c.win ? '用 winget 或官方安裝程式' : '從發行版套件庫或官方儲存庫'}安裝 ${c.engine}，設定為開機啟動，並只監聽 127.0.0.1。建立資料庫\n   bf_game_db（utf8mb4）和使用者 ${q(c.user)}，${c.password ? `密碼為 ${q(c.password)}` : '密碼由你隨機產生一個強密碼'}，只授予 bf_game_db 的全部權限。如果安裝需要 root 密碼，另外產生一個強密碼。`,
  dockerSetup: () => `已經安裝了 Docker Engine 和 Docker Compose 模組：檢查 ${q('docker compose version')} 能正常執行，並且 docker 服務\n   會開機啟動。不要重新安裝 Docker。`,
  dockerRun: (c, cmd) => `用專案內建的腳本在 Docker 中安裝並啟動伺服器。${c.dockerInstall ? '加上 --install-docker 時，它會先用 Docker 官方腳本安裝\n   Docker Engine 和 Compose 模組，並啟用 docker 服務。' : ''}它會下載並校驗安裝套件，建置本機映像檔，並用 Docker Compose 啟動${c.dockerDb ? '，\n   同時啟動一個只監聽 127.0.0.1:3306 的 MariaDB 11.4 資料庫容器，包含資料庫 bf_game_db、使用者 bff 和隨機密碼' : ''}：\n   ${cmd}\n   以 root 執行時，容器設定儲存在 /opt/bubblefighter-fantasy/docker，Compose 會讓容器隨系統啟動。\n   然後等待 ${q('curl -s http://127.0.0.1:7300/health')} 回傳 "ok"。`,
  existing: c => `已經安裝了 MySQL 或 MariaDB：不要重新安裝或修改它的設定。檢查它正在執行，並且使用者 ${q(c.user)}\n   能在 127.0.0.1:3306 登入${c.password ? `，密碼為 ${q(c.password)}` : '（向我詢問密碼）'}。如果資料庫 bf_game_db 不存在，設定頁面會建立它。`,
  sqlite: '不要安裝資料庫伺服器；我會在設定頁面選擇 SQLite。',
  rules: c => '不要自己操作、完成或跳過 WebUI 上的設定精靈（不要呼叫 API、不要寫入狀態或設定檔、不要用 run --profile）：\n   除非我要求，由我在瀏覽器中完成。' + (c.pc
    ? '不要修改防火牆、防毒軟體或系統安全設定，也不要變更無關的檔案或程式。'
    : '不要修改防火牆、防毒軟體或 SSH 設定，不要自行開放連接埠，也不要變更無關的檔案或服務。'),
  summary: c => `最後用簡短的 Markdown 總結：版本、安裝位置、如何啟動和停止伺服器${c.db === 'sqlite'
    ? '、提醒我在設定頁面的資料庫步驟選擇 SQLite'
    : `、設定頁面資料庫步驟要填寫的內容\n   （MySQL，主機 127.0.0.1，連接埠 3306，資料庫 bf_game_db，${c.dockerDb ? '使用者 bff，密碼為 /opt/bubblefighter-fantasy/docker/.env 中 BFF_MYSQL_PASSWORD 的值' : `使用者 ${c.user} 及其密碼${c.db === 'install' ? '，以及你產生的密碼' : ''}`}）`}${c.pc ? '' : '、\n   需要在防火牆或雲端安全群組中為玩家開放的連接埠（我在設定頁面選擇的每個用戶端版本的遊戲連接埠，例如 kr-1256 是 7100，\n   kr-812 是 7110）'}，以及我在設定頁面接下來該做什麼。`,
}

const ko: Words = {
  intro: c => c.remote
    ? `내 ${c.os} 서버에 Bubble Fighter Fantasy 서버를 설치해 주세요. 당신은 서버가 아니라 내 컴퓨터에서 실행되고 있으므로 모든 단계를 SSH로 서버에서 수행하세요.`
    : `이 ${c.os} ${c.pc ? 'PC' : '서버'}에 Bubble Fighter Fantasy 서버를 설치해 주세요.`,
  ssh: c => `${c.known ? '' : '먼저 서버 주소와 SSH 사용자를 물어본 다음 '}${q(`ssh ${c.ssh}`)}로 접속하고, 아래의 모든 명령은 이 컴퓨터가 아니라 서버에서 실행하세요.${c.win ? ' Windows 서버에는 OpenSSH Server가 필요하며 명령은 PowerShell에서 실행됩니다.' : ''} 기존 SSH 키를 사용하세요. 접속이 비밀번호를 요구하거나 실패하면 멈추고 키 로그인 설정 방법을 알려 주세요. 이 대화에 비밀번호를 입력하라고 요청하지 마세요.`,
  guide: `프로젝트: ${project}. 시작하기 전에 배포 가이드 ${skill}를 읽고 따르세요. 설치 스크립트의 모든 옵션과\n   자주 발생하는 문제의 해결 방법이 나와 있습니다.`,
  winScript: (c, cmd) => `프로젝트의 공식 스크립트로 서버를 설치하세요. ${c.pc ? 'PowerShell' : '관리자 PowerShell(-Service에 필요합니다. 셸이 관리자 권한이 아니면\n   멈추고 관리자 권한으로 명령을 실행해 달라고 요청하세요)'}에서 실행합니다. 프로세서를 확인하고 맞는 패키지와 SHA256SUMS를\n   내려받아 검증하며, ASP.NET Core Runtime 10이 없으면 설치하고 C:\\BubbleFighterFantasy에 설치합니다:\n   ${cmd}\n   ${c.pc
    ? '서버를 별도 창에서 시작하고 http://127.0.0.1:7300/health가 "ok"를 반환할 때까지 기다린 뒤 브라우저에서 설정 페이지를 엽니다.'
    : '예약 작업 BubbleFighterFantasy(SYSTEM으로 실행, Windows와 함께 시작, 접속을 끊어도 계속 실행)를 등록하고\n   시작한 다음 http://127.0.0.1:7300/health가 "ok"를 반환할 때까지 기다립니다.'}\n   스크립트가 오류로 멈추면 오류를 읽고 배포 가이드로 원인을 해결한 뒤 같은 명령을 다시 실행하세요.`,
  openOnServer: '첫 실행 설정을 위해 이 서버의 브라우저에서 http://127.0.0.1:7300/을 여세요.',
  tunnel: c => `7300 포트(설정 페이지와 WebUI)는 인터넷에 열지 마세요. 내 컴퓨터에서 SSH 터널로 여는 방법을 알려 주세요:\n   ${q(`ssh -L 7300:127.0.0.1:7300 ${c.ssh}`)} 실행 후 브라우저에서 http://127.0.0.1:7300/.`,
  linuxInstall: (c, cmd) => `프로젝트의 공식 스크립트로 서버를 설치하세요. 프로세서를 확인하고 맞는 패키지와 SHA256SUMS를 내려받아 검증하며,\n   ASP.NET Core Runtime 10이 없으면 설치합니다(curl이 없으면 먼저 설치하세요):\n   ${cmd}\n   ${c.pc
    ? '~/bubblefighter-fantasy에 설치됩니다. 그런 다음 ~/bubblefighter-fantasy/bff를 백그라운드로 시작하고(예: nohup,\n   로그는 ~/bubblefighter-fantasy/bff.log) '
    : 'root로 실행하면 /opt/bubblefighter-fantasy에 설치되고 bubblefighter-fantasy systemd 서비스가 생성되어 시작됩니다.\n   그런 다음 '}${q('curl -s http://127.0.0.1:7300/health')}가 "ok"를 반환할 때까지 기다리세요.`,
  openPc: '첫 실행 설정을 위해 내 브라우저에서 http://127.0.0.1:7300/을 열어 주세요.',
  install: c => `${c.engine}을(를) ${c.win ? 'winget 또는 공식 설치 프로그램으로' : '배포판 패키지 또는 공식 저장소에서'} 설치하고, 시스템과 함께 시작되며 127.0.0.1에서만\n   수신하도록 하세요. 데이터베이스 bf_game_db(utf8mb4)와 사용자 ${q(c.user)}를 만들고 ${c.password ? `비밀번호는 ${q(c.password)}로 하며` : '비밀번호는 강력한 임의 값으로 생성하고'}, bf_game_db에 대한\n   모든 권한만 부여하세요. 설치 중 root 비밀번호가 필요하면 다른 강력한 비밀번호를 생성하세요.`,
  dockerSetup: () => `Docker Engine과 Docker Compose 플러그인이 이미 설치되어 있습니다. ${q('docker compose version')}이 동작하는지, docker 서비스가\n   시스템과 함께 시작되는지 확인하세요. Docker를 다시 설치하지 마세요.`,
  dockerRun: (c, cmd) => `프로젝트의 공식 스크립트로 서버를 Docker에 설치하고 시작하세요.${c.dockerInstall ? ' --install-docker를 붙이면 먼저 Docker 공식 스크립트로\n   Docker Engine과 Compose 플러그인을 설치하고 docker 서비스를 활성화합니다.' : ''} 패키지를 내려받아 검증하고 로컬 이미지를 빌드한 뒤 Docker Compose로\n   시작합니다${c.dockerDb ? '. 127.0.0.1:3306에서만 수신하는 MariaDB 11.4 데이터베이스 컨테이너도 함께 시작되며, 데이터베이스 bf_game_db,\n   사용자 bff와 임의 비밀번호가 만들어집니다' : ''}:\n   ${cmd}\n   root로 실행하면 스택은 /opt/bubblefighter-fantasy/docker에 있고 Compose가 시스템과 함께 컨테이너를 다시 시작합니다.\n   그런 다음 ${q('curl -s http://127.0.0.1:7300/health')}가 "ok"를 반환할 때까지 기다리세요.`,
  existing: c => `MySQL 또는 MariaDB가 이미 설치되어 있습니다. 다시 설치하거나 설정을 바꾸지 마세요. 실행 중인지, 그리고 사용자\n   ${q(c.user)}가 127.0.0.1:3306에 로그인할 수 있는지 확인하세요${c.password ? `(비밀번호 ${q(c.password)})` : '(비밀번호는 저에게 물어보세요)'}. 데이터베이스 bf_game_db가 없으면 설정 페이지가 만듭니다.`,
  sqlite: '데이터베이스 서버는 설치하지 마세요. 설정 페이지에서 SQLite를 선택하겠습니다.',
  rules: c => 'WebUI의 설정 마법사를 직접 진행하거나 완료하거나 건너뛰지 마세요(API 호출, 상태·설정 파일 작성, run --profile 금지).\n   제가 요청하지 않는 한 제가 브라우저에서 마칩니다. ' + (c.pc
    ? '방화벽, 백신 또는 시스템 보안 설정은 바꾸지 말고, 관계없는 파일이나 프로그램은 건드리지 마세요.'
    : '방화벽, 백신 또는 SSH 설정은 바꾸지 말고, 포트를 직접 열지 말며, 관계없는 파일이나 서비스는 건드리지 마세요.'),
  summary: c => `마지막에 짧은 Markdown 요약을 주세요: 버전, 설치 폴더, 서버 시작과 중지 방법${c.db === 'sqlite'
    ? ', 설정 페이지의 데이터베이스 단계에서 SQLite를 선택하라는 안내'
    : `, 설정 페이지의 데이터베이스 단계에 입력할 값\n   (MySQL, 호스트 127.0.0.1, 포트 3306, 데이터베이스 bf_game_db, ${c.dockerDb ? '사용자 bff, 비밀번호는 /opt/bubblefighter-fantasy/docker/.env의 BFF_MYSQL_PASSWORD 값' : `사용자 ${c.user}와 비밀번호${c.db === 'install' ? ', 생성한 비밀번호 포함' : ''}`})`}${c.pc ? '' : ',\n   플레이어를 위해 방화벽이나 클라우드 보안 그룹에서 열어야 할 포트(설정 페이지에서 선택한 각 클라이언트 버전의 게임 포트,\n   예: kr-1256은 7100, kr-812는 7110)'}, 설정 페이지에서 다음에 할 일.`,
}

const words: Record<Code, Words> = { en, 'zh-CN': zhCN, 'zh-TW': zhTW, ko }

/** The prompt for these answers in the page language. */
export function buildPrompt(choice: PromptChoice, lang: Code): string {
  const w = words[lang]
  const pc = choice.target === 'pc'
  const host = choice.host.trim(), sshUser = choice.sshUser.trim()
  const c: Ctx = {
    win: choice.os === 'windows',
    pc,
    remote: !pc && choice.agent === 'pc',
    os: choice.os === 'windows' ? 'Windows' : 'Linux',
    ssh: `${sshUser || '<user>'}@${host || '<server>'}`,
    known: !!host && !!sshUser,
    engine: engines[choice.engine],
    user: choice.dbUser.trim() || (choice.db === 'existing' ? 'root' : 'bff'),
    password: choice.dbPassword,
    db: choice.db,
    docker: false,
    dockerInstall: choice.docker === 'install',
    dockerDb: false,
  }
  // Docker applies to Linux servers; its database container replaces a separately installed one.
  c.docker = !pc && choice.os === 'linux' && choice.runtime === 'docker'
  c.dockerDb = c.docker && choice.db === 'install'
  const db = choice.db === 'sqlite' ? w.sqlite : choice.db === 'install' ? w.install(c) : w.existing(c)
  const steps: string[] = [w.guide]
  if (c.remote) steps.push(w.ssh(c))
  if (c.win) {
    // Windows PowerShell 5.1 and 7 both run the script this way; -NoPrompt because an agent has no console.
    const cmd = `& ([scriptblock]::Create((irm ${scriptPs}))) -NoPrompt ${pc ? '-Start' : '-Service'}`
    steps.push(db, w.winScript(c, cmd))
    if (!pc) steps.push(c.remote ? w.tunnel(c) : w.openOnServer)
  } else if (c.docker) {
    const cmd = `curl -fsSL ${scriptSh} | sudo bash -s -- --no-prompt --docker${c.dockerInstall ? ' --install-docker' : ''}${c.dockerDb ? ' --with-mysql' : ''}`
    steps.push(...(c.dockerInstall ? [] : [w.dockerSetup()]), ...(c.dockerDb ? [] : [db]), w.dockerRun(c, cmd), w.tunnel(c))
  } else {
    const cmd = pc ? `curl -fsSL ${scriptSh} | bash -s -- --no-prompt` : `curl -fsSL ${scriptSh} | sudo bash -s -- --no-prompt --service`
    steps.push(db, w.linuxInstall(c, cmd), pc ? w.openPc : w.tunnel(c))
  }
  steps.push(w.rules(c), w.summary(c))
  return [w.intro(c), ...steps.map((s, i) => `${i + 1}. ${s}`)].join('\n')
}
