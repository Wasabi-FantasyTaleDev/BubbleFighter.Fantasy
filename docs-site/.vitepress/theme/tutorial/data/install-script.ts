import type { Step, Tutorial } from '../types'

// Install Bubble Fighter Fantasy with the project's own script, no agent needed: on Linux over SSH from
// a terminal (the default), on Windows in PowerShell. The console lines are what deploy/install.sh and
// deploy/install.ps1 print for a first install without options; the switch beside the page title picks
// the system, and the commands for it are shown under the player.

const windows: Step[] = [
    {
      id: 'open',
      scene: 'script',
      props: { phase: 'open' },
      title: { en: 'Open Windows PowerShell', 'zh-CN': '打开 Windows PowerShell', 'zh-TW': '開啟 Windows PowerShell', ko: 'Windows PowerShell 열기' },
      duration: 8000,
      cues: { searchClick: 1200, flyout: 1350, typeStart: 1700, typeEnd: 3000, resultHover: 4300, resultClick: 5200, console: 5400 },
      cursor: [
        { at: 500, x: 760, y: 520 },
        { at: 1200, target: 'tb-search', dx: -30, click: true, move: 900 },
        { at: 4300, target: 'ps-result', dx: -60, move: 900 },
        { at: 5200, target: 'ps-result', dx: -60, click: true, move: 200 },
        { at: 6400, x: 980, y: 620, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 420, z: 1, mz: 1.5 },
        { at: 1000, x: 640, y: 560, z: 1.12, mz: 2 },
        { at: 5500, x: 640, y: 360, z: 1, mz: 1.5 },
      ],
      captions: [
        { at: 0, text: { en: 'Click the search box on the taskbar.', 'zh-CN': '点击任务栏上的搜索框。', 'zh-TW': '按一下工作列上的搜尋方塊。', ko: '작업 표시줄의 검색 상자를 클릭합니다.' } },
        { at: 1700, text: { en: 'Type PowerShell and open Windows PowerShell. No administrator rights are needed on a personal PC.', 'zh-CN': '输入 PowerShell，打开 Windows PowerShell。在个人电脑上不需要管理员权限。', 'zh-TW': '輸入 PowerShell，開啟 Windows PowerShell。在個人電腦上不需要系統管理員權限。', ko: 'PowerShell을 입력하고 Windows PowerShell을 엽니다. 개인 PC에서는 관리자 권한이 필요 없습니다.' } },
        { at: 5600, text: { en: 'A server that should start with Windows needs “Run as administrator” instead; see Commands at the top right.', 'zh-CN': '如果要让服务器随 Windows 启动，请改用“以管理员身份运行”，命令见右上角的“命令”。', 'zh-TW': '如果要讓伺服器隨 Windows 啟動，請改用「以系統管理員身分執行」，命令見右上角的「命令」。', ko: 'Windows와 함께 시작되는 서버라면 “관리자 권한으로 실행”으로 여세요. 명령은 오른쪽 위의 명령에 있습니다.' } },
      ],
    },
    {
      id: 'paste',
      scene: 'script',
      props: { phase: 'paste' },
      title: { en: 'Paste the install command and run it', 'zh-CN': '粘贴安装命令并运行', 'zh-TW': '貼上安裝命令並執行', ko: '설치 명령을 붙여 넣고 실행' },
      duration: 10000,
      cues: { cmdStart: 1300, cmdEnd: 1700, cmdEnter: 3200, folderQ: 3900, folderEnter: 7600 },
      cursor: [
        { at: 300, x: 900, y: 500 },
        { at: 1200, target: 'console', dx: -100, dy: -60, click: true, move: 900 },
        { at: 2400, x: 1060, y: 560, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 240, z: 1.12, mz: 2 },
        { at: 3600, target: 'sc-last', x: 640, z: 1.12, mz: 2 },
      ],
      captions: [
        { at: 0, text: { en: 'Copy the Windows command from Commands at the top right, then right-click in PowerShell to paste it.', 'zh-CN': '从右上角的“命令”复制 Windows 命令，然后在 PowerShell 里单击右键粘贴。', 'zh-TW': '從右上角的「命令」複製 Windows 命令，然後在 PowerShell 裡按右鍵貼上。', ko: '오른쪽 위의 명령에서 Windows 명령을 복사한 뒤 PowerShell에서 마우스 오른쪽 버튼을 눌러 붙여 넣습니다.' } },
        { at: 2400, text: { en: 'Press Enter. The command downloads the official install script from GitHub and runs it.', 'zh-CN': '按 Enter。命令会从 GitHub 下载官方安装脚本并运行。', 'zh-TW': '按 Enter。命令會從 GitHub 下載官方安裝腳本並執行。', ko: 'Enter를 누릅니다. 명령이 GitHub에서 공식 설치 스크립트를 내려받아 실행합니다.' } },
        { at: 3900, text: { en: 'The script asks a few questions; the answer in capitals is the default. Press Enter to keep the short folder C:\\BubbleFighterFantasy.', 'zh-CN': '脚本会问几个问题，大写的选项是默认值。按 Enter 保留短路径 C:\\BubbleFighterFantasy。', 'zh-TW': '腳本會問幾個問題，大寫的選項是預設值。按 Enter 保留短路徑 C:\\BubbleFighterFantasy。', ko: '스크립트가 몇 가지를 묻습니다. 대문자 답이 기본값입니다. Enter를 눌러 짧은 폴더 C:\\BubbleFighterFantasy를 그대로 씁니다.' } },
      ],
    },
    {
      id: 'install',
      scene: 'script',
      props: { phase: 'install' },
      title: { en: 'The script downloads, verifies and installs', 'zh-CN': '脚本下载、校验并安装', 'zh-TW': '腳本下載、校驗並安裝', ko: '스크립트가 내려받고 검증하고 설치' },
      duration: 11000,
      cues: { fetch: 600, check: 3000, runtime: 4300, runtimeDone: 7600, unpack: 8600 },
      cursor: [{ at: 0, x: 1060, y: 560, hide: true }],
      camera: [
        { at: 0, target: 'sc-last', x: 640, z: 1.12, mz: 2 },
        { at: 4300, target: 'sc-last', x: 640, z: 1.12, mz: 2, dur: 900 },
        { at: 8600, target: 'sc-last', x: 640, z: 1.12, mz: 2, dur: 900 },
      ],
      captions: [
        { at: 0, text: { en: 'It picks the package for your processor and downloads it with SHA256SUMS.', 'zh-CN': '脚本按处理器选择安装包，并连同 SHA256SUMS 一起下载。', 'zh-TW': '腳本依處理器選擇安裝套件，並連同 SHA256SUMS 一起下載。', ko: '프로세서에 맞는 패키지를 골라 SHA256SUMS와 함께 내려받습니다.' } },
        { at: 3000, text: { en: 'It checks the checksum and stops if the download is damaged.', 'zh-CN': '它会校验 SHA256，下载损坏时就停止。', 'zh-TW': '它會校驗 SHA256，下載損毀時就停止。', ko: '체크섬을 확인하고, 다운로드가 손상되었으면 멈춥니다.' } },
        { at: 4300, text: { en: 'The server is a .NET program. Without the ASP.NET Core Runtime 10, the script installs a private copy in the install folder.', 'zh-CN': '服务器是 .NET 程序。缺少 ASP.NET Core Runtime 10 时，脚本会在安装文件夹里装一份私有副本。', 'zh-TW': '伺服器是 .NET 程式。缺少 ASP.NET Core Runtime 10 時，腳本會在安裝資料夾裡裝一份私有副本。', ko: '서버는 .NET 프로그램입니다. ASP.NET Core Runtime 10이 없으면 스크립트가 설치 폴더에 전용 사본을 설치합니다.' } },
        { at: 8600, text: { en: 'It unpacks the release and checks that it starts before switching to it.', 'zh-CN': '它解压这个版本，确认能启动后才切换过去。', 'zh-TW': '它解壓縮這個版本，確認能啟動後才切換過去。', ko: '릴리스를 풀고, 시작되는지 확인한 뒤에 전환합니다.' } },
      ],
    },
    {
      id: 'answer',
      scene: 'script',
      props: { phase: 'answer' },
      title: { en: 'Answer the questions and start the server', 'zh-CN': '回答问题并启动服务器', 'zh-TW': '回答問題並啟動伺服器', ko: '질문에 답하고 서버 시작' },
      duration: 12500,
      cues: { remoteQ: 500, remoteEnter: 2800, tokenEnter: 4600, startEnter: 6000, server: 7200, summary: 8600 },
      cursor: [{ at: 0, x: 1060, y: 560, hide: true }],
      camera: [
        { at: 0, target: 'sc-last', x: 640, z: 1.12, mz: 2 },
        { at: 6800, x: 820, y: 460, z: 1.1, mz: 1.8, dur: 1000 },
        { at: 8600, x: 640, y: 330, z: 1.02, mz: 1.6, dur: 1000 },
      ],
      captions: [
        { at: 0, text: { en: 'Should other machines open the WebUI? Press Enter for yes (the default), so you can finish the setup from another computer too.', 'zh-CN': '是否允许其他电脑打开 WebUI？按 Enter 表示是（默认），这样也能从别的电脑完成设置。', 'zh-TW': '是否允許其他電腦開啟 WebUI？按 Enter 表示是（預設），這樣也能從別的電腦完成設定。', ko: '다른 컴퓨터에서 WebUI를 열 수 있게 할까요? Enter를 누르면 예(기본값)이므로 다른 컴퓨터에서도 설정을 마칠 수 있습니다.' } },
        { at: 2900, text: { en: 'A setup token? Press Enter for no. Finish the setup right after installing: until an administrator exists, anyone who reaches the page can do it.', 'zh-CN': '需要设置令牌吗？按 Enter 表示否。安装后请马上完成设置：管理员创建之前，任何能打开页面的人都能操作。', 'zh-TW': '需要設定權杖嗎？按 Enter 表示否。安裝後請馬上完成設定：管理員建立之前，任何能開啟頁面的人都能操作。', ko: '설정 토큰이 필요할까요? Enter를 누르면 아니요입니다. 설치 후 바로 설정을 마치세요. 관리자가 생기기 전에는 페이지에 접속하는 누구나 진행할 수 있습니다.' } },
        { at: 4700, text: { en: 'Start the server now? Press Enter for yes.', 'zh-CN': '现在启动服务器吗？按 Enter 表示是。', 'zh-TW': '現在啟動伺服器嗎？按 Enter 表示是。', ko: '지금 서버를 시작할까요? Enter를 누르면 예입니다.' } },
        { at: 7200, text: { en: 'The server starts in its own window. Closing that window stops it.', 'zh-CN': '服务器在单独的窗口中启动，关闭这个窗口就会停止服务器。', 'zh-TW': '伺服器在獨立的視窗中啟動，關閉這個視窗就會停止伺服器。', ko: '서버가 별도 창에서 시작됩니다. 그 창을 닫으면 서버가 멈춥니다.' } },
        { at: 8600, text: { en: 'The summary says where it is installed, how to start it again and how to upgrade: run the same command again.', 'zh-CN': '总结里写着安装位置、如何再次启动，以及如何升级：再运行一次同一条命令。', 'zh-TW': '總結裡寫著安裝位置、如何再次啟動，以及如何升級：再執行一次同一個命令。', ko: '요약에 설치 위치, 다시 시작하는 방법, 업그레이드 방법(같은 명령을 다시 실행)이 나옵니다.' } },
      ],
    },
    {
      id: 'setup',
      scene: 'script',
      props: { phase: 'setup' },
      title: { en: 'Finish the setup wizard in the browser', 'zh-CN': '在浏览器中完成设置向导', 'zh-TW': '在瀏覽器中完成設定精靈', ko: '브라우저에서 설정 마법사 완료' },
      duration: 9000,
      cues: { browser: 700, contClick: 2600, assist: 2800, pick: 4600 },
      cursor: [
        { at: 1200, x: 900, y: 640 },
        { at: 2600, target: 'sc-continue', click: true, move: 1000 },
        { at: 4600, target: 'sc-pick', dx: -200, click: true, move: 1100 },
        { at: 6400, x: 980, y: 660, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 380, z: 1, mz: 1.5 },
        { at: 3000, target: 'sc-card', z: 1.05, mz: 1.8 },
      ],
      captions: [
        { at: 0, text: { en: 'The script opens the setup page, http://127.0.0.1:7300/, in your browser.', 'zh-CN': '脚本会在浏览器中打开设置页面 http://127.0.0.1:7300/。', 'zh-TW': '腳本會在瀏覽器中開啟設定頁面 http://127.0.0.1:7300/。', ko: '스크립트가 브라우저에서 설정 페이지 http://127.0.0.1:7300/을 엽니다.' } },
        { at: 3000, text: { en: 'Go through the wizard yourself: language, server name, client versions, client data, database and the administrator account.', 'zh-CN': '自己走完设置向导：语言、服务器名称、客户端版本、客户端数据、数据库和管理员账号。', 'zh-TW': '自己走完設定精靈：語言、伺服器名稱、用戶端版本、用戶端資料、資料庫和管理員帳號。', ko: '설정 마법사를 직접 진행하세요: 언어, 서버 이름, 클라이언트 버전, 클라이언트 데이터, 데이터베이스, 관리자 계정.' } },
        { at: 6400, text: { en: 'On your own PC, choose SQLite at the database step: it needs no database server.', 'zh-CN': '在自己的电脑上，数据库步骤请选择 SQLite：不需要数据库服务器。', 'zh-TW': '在自己的電腦上，資料庫步驟請選擇 SQLite：不需要資料庫伺服器。', ko: '내 PC라면 데이터베이스 단계에서 SQLite를 선택하세요. 데이터베이스 서버가 필요 없습니다.' } },
      ],
    },
]

const linux: Step[] = [
  {
    id: 'ssh',
    scene: 'linux',
    props: { phase: 'open' },
    title: { en: 'Open Terminal and sign in to the server', 'zh-CN': '打开终端并登录服务器', 'zh-TW': '開啟終端機並登入伺服器', ko: '터미널을 열고 서버에 로그인' },
    duration: 10500,
    cues: { dockClick: 2400, term: 2600, sshStart: 4400, sshEnd: 5800, sshEnter: 6400, login: 7200 },
    cursor: [
      { at: 400, x: 900, y: 520 },
      { at: 2400, target: 'dock-terminal', click: true, move: 1300 },
      { at: 3400, x: 1000, y: 700, move: 800 },
      { at: 4000, x: 1000, y: 700, hide: true },
    ],
    camera: [
      { at: 0, x: 640, y: 400, z: 1, mz: 1.2 },
      { at: 1500, target: 'dock-terminal', y: 640, z: 1.35, mz: 2.2, dur: 900 },
      { at: 3000, x: 640, y: 380, z: 1.4, mz: 2, dur: 1300 },
      { at: 7300, target: 'sc-last', x: 640, z: 1.4, mz: 2, dur: 900 },
    ],
    captions: [
      { at: 0, text: { en: 'On your computer, open a terminal. On a Mac, click Terminal in the Dock (on Windows, use PowerShell).', 'zh-CN': '在你的电脑上打开终端。Mac 上点击程序坞里的“终端”（Windows 用 PowerShell）。', 'zh-TW': '在你的電腦上開啟終端機。Mac 上點選 Dock 裡的「終端機」（Windows 用 PowerShell）。', ko: '내 컴퓨터에서 터미널을 엽니다. Mac에서는 Dock의 터미널을 클릭하세요(Windows는 PowerShell).' } },
      { at: 4400, text: { en: 'Sign in to the server as root over SSH (here 203.0.113.10 stands for your server\'s address).', 'zh-CN': '用 SSH 以 root 登录服务器（这里的 203.0.113.10 代表你的服务器地址）。', 'zh-TW': '用 SSH 以 root 登入伺服器（這裡的 203.0.113.10 代表你的伺服器位址）。', ko: 'SSH로 서버에 root로 로그인합니다(여기서 203.0.113.10은 내 서버 주소를 뜻합니다).' } },
      { at: 7300, text: { en: 'Any recent x86_64 or aarch64 Linux works; you are now typing on the server.', 'zh-CN': '常见的 x86_64 或 aarch64 Linux 都可以；现在你输入的命令在服务器上运行。', 'zh-TW': '常見的 x86_64 或 aarch64 Linux 都可以；現在你輸入的命令在伺服器上執行。', ko: '최근의 x86_64 또는 aarch64 Linux라면 됩니다. 이제 입력하는 명령은 서버에서 실행됩니다.' } },
    ],
  },
  {
    id: 'paste',
    scene: 'linux',
    props: { phase: 'paste' },
    title: { en: 'Paste the install command and choose how to install', 'zh-CN': '粘贴安装命令并选择安装方式', 'zh-TW': '貼上安裝命令並選擇安裝方式', ko: '설치 명령을 붙여 넣고 설치 방식 선택' },
    duration: 9500,
    cues: { cmdStart: 900, cmdEnd: 1300, cmdEnter: 2700, intro: 3300, dockerEnter: 5400, serviceEnter: 7400 },
    cursor: [{ at: 0, x: 1100, y: 700, hide: true }],
    camera: [{ at: 0, target: 'sc-last', x: 640, z: 1.4, mz: 2 }, { at: 3300, target: 'sc-last', x: 640, z: 1.4, mz: 2, dur: 900 }],
    captions: [
      { at: 0, text: { en: 'Copy the Linux command from Commands at the top right, paste it and press Enter.', 'zh-CN': '从右上角的“命令”复制 Linux 命令，粘贴后按 Enter。', 'zh-TW': '從右上角的「命令」複製 Linux 命令，貼上後按 Enter。', ko: '오른쪽 위의 명령에서 Linux 명령을 복사해 붙여 넣고 Enter를 누릅니다.' } },
      { at: 3300, text: { en: 'Docker? Press Enter for no: the server is installed directly on the system.', 'zh-CN': '使用 Docker 吗？按 Enter 表示否：服务器直接安装在系统上。', 'zh-TW': '使用 Docker 嗎？按 Enter 表示否：伺服器直接安裝在系統上。', ko: 'Docker를 쓸까요? Enter를 누르면 아니요이고, 서버를 시스템에 직접 설치합니다.' } },
      { at: 5500, text: { en: 'A systemd service? Press Enter for yes: the server starts with the machine and keeps running after you sign out.', 'zh-CN': '创建 systemd 服务吗？按 Enter 表示是：服务器随系统启动，退出登录后继续运行。', 'zh-TW': '建立 systemd 服務嗎？按 Enter 表示是：伺服器隨系統啟動，登出後繼續執行。', ko: 'systemd 서비스를 만들까요? Enter를 누르면 예이고, 서버가 부팅과 함께 시작되며 로그아웃해도 계속 실행됩니다.' } },
    ],
  },
  {
    id: 'install',
    scene: 'linux',
    props: { phase: 'install' },
    title: { en: 'The script downloads, verifies and installs', 'zh-CN': '脚本下载、校验并安装', 'zh-TW': '腳本下載、校驗並安裝', ko: '스크립트가 내려받고 검증하고 설치' },
    duration: 11000,
    cues: { fetch: 600, check: 3000, runtime: 4300, runtimeDone: 7600, unpack: 8600 },
    cursor: [{ at: 0, x: 1100, y: 700, hide: true }],
    camera: [
      { at: 0, target: 'sc-last', x: 640, z: 1.4, mz: 2 },
      { at: 4300, target: 'sc-last', x: 640, z: 1.4, mz: 2, dur: 900 },
      { at: 8600, target: 'sc-last', x: 640, z: 1.4, mz: 2, dur: 900 },
    ],
    captions: [
      { at: 0, text: { en: 'It picks the package for the server\'s processor (x86_64 or aarch64) and downloads it with SHA256SUMS.', 'zh-CN': '脚本按服务器的处理器（x86_64 或 aarch64）选择安装包，并连同 SHA256SUMS 一起下载。', 'zh-TW': '腳本依伺服器的處理器（x86_64 或 aarch64）選擇安裝套件，並連同 SHA256SUMS 一起下載。', ko: '서버의 프로세서(x86_64 또는 aarch64)에 맞는 패키지를 골라 SHA256SUMS와 함께 내려받습니다.' } },
      { at: 3000, text: { en: 'It checks the checksum and stops if the download is damaged.', 'zh-CN': '它会校验 SHA256，下载损坏时就停止。', 'zh-TW': '它會校驗 SHA256，下載損毀時就停止。', ko: '체크섬을 확인하고, 다운로드가 손상되었으면 멈춥니다.' } },
      { at: 4300, text: { en: 'The server is a .NET program. Without the ASP.NET Core Runtime 10, the script installs a private copy and leaves the system\'s .NET alone.', 'zh-CN': '服务器是 .NET 程序。缺少 ASP.NET Core Runtime 10 时，脚本会装一份私有副本，不改动系统的 .NET。', 'zh-TW': '伺服器是 .NET 程式。缺少 ASP.NET Core Runtime 10 時，腳本會裝一份私有副本，不變動系統的 .NET。', ko: '서버는 .NET 프로그램입니다. ASP.NET Core Runtime 10이 없으면 스크립트가 전용 사본을 설치하고 시스템의 .NET은 건드리지 않습니다.' } },
      { at: 8600, text: { en: 'It unpacks the release and checks that it starts before switching to it.', 'zh-CN': '它解压这个版本，确认能启动后才切换过去。', 'zh-TW': '它解壓縮這個版本，確認能啟動後才切換過去。', ko: '릴리스를 풀고, 시작되는지 확인한 뒤에 전환합니다.' } },
    ],
  },
  {
    id: 'answer',
    scene: 'linux',
    props: { phase: 'answer' },
    title: { en: 'Answer the WebUI questions; the service starts', 'zh-CN': '回答 WebUI 问题，服务随即启动', 'zh-TW': '回答 WebUI 問題，服務隨即啟動', ko: 'WebUI 질문에 답하면 서비스가 시작됨' },
    duration: 11500,
    cues: { remoteQ: 500, remoteEnter: 2800, tokenEnter: 4600, service: 5400, summary: 7200 },
    cursor: [{ at: 0, x: 1100, y: 700, hide: true }],
    camera: [
      { at: 0, target: 'sc-last', x: 640, z: 1.4, mz: 2 },
      { at: 7200, x: 640, y: 380, z: 1.3, mz: 1.8, dur: 1000 },
    ],
    captions: [
      { at: 0, text: { en: 'Should other machines open the WebUI? Press Enter for yes (the default), so you can finish the setup from your own computer.', 'zh-CN': '是否允许其他电脑打开 WebUI？按 Enter 表示是（默认），这样你能在自己的电脑上完成设置。', 'zh-TW': '是否允許其他電腦開啟 WebUI？按 Enter 表示是（預設），這樣你能在自己的電腦上完成設定。', ko: '다른 컴퓨터에서 WebUI를 열 수 있게 할까요? Enter를 누르면 예(기본값)이므로 내 컴퓨터에서 설정을 마칠 수 있습니다.' } },
      { at: 2900, text: { en: 'A setup token? Press Enter for no. Finish the setup right after installing: until an administrator exists, anyone who reaches the page can do it.', 'zh-CN': '需要设置令牌吗？按 Enter 表示否。安装后请马上完成设置：管理员创建之前，任何能打开页面的人都能操作。', 'zh-TW': '需要設定權杖嗎？按 Enter 表示否。安裝後請馬上完成設定：管理員建立之前，任何能開啟頁面的人都能操作。', ko: '설정 토큰이 필요할까요? Enter를 누르면 아니요입니다. 설치 후 바로 설정을 마치세요. 관리자가 생기기 전에는 페이지에 접속하는 누구나 진행할 수 있습니다.' } },
      { at: 5400, text: { en: 'The script installs the bubblefighter-fantasy service and starts it.', 'zh-CN': '脚本安装并启动 bubblefighter-fantasy 服务。', 'zh-TW': '腳本安裝並啟動 bubblefighter-fantasy 服務。', ko: '스크립트가 bubblefighter-fantasy 서비스를 설치하고 시작합니다.' } },
      { at: 7200, text: { en: 'Open TCP port 7300 in the firewall or cloud security group, or tunnel it: ssh -L 7300:127.0.0.1:7300 root@your-server.', 'zh-CN': '在防火墙或云安全组中放行 TCP 7300，或者用隧道：ssh -L 7300:127.0.0.1:7300 root@你的服务器。', 'zh-TW': '在防火牆或雲端安全群組中開放 TCP 7300，或者用通道：ssh -L 7300:127.0.0.1:7300 root@你的伺服器。', ko: '방화벽이나 클라우드 보안 그룹에서 TCP 7300을 열거나 터널을 쓰세요: ssh -L 7300:127.0.0.1:7300 root@내-서버.' } },
    ],
  },
  {
    id: 'setup',
    scene: 'linux',
    props: { phase: 'setup' },
    title: { en: 'Finish the setup wizard in the browser', 'zh-CN': '在浏览器中完成设置向导', 'zh-TW': '在瀏覽器中完成設定精靈', ko: '브라우저에서 설정 마법사 완료' },
    duration: 9000,
    cues: { browser: 700, contClick: 2600, assist: 2800, pick: 4600 },
    cursor: [
      { at: 1200, x: 900, y: 640 },
      { at: 2600, target: 'sc-continue', click: true, move: 1000 },
      { at: 4600, target: 'sc-pick', dx: -200, click: true, move: 1100 },
      { at: 6400, x: 980, y: 660, move: 900 },
    ],
    camera: [
      { at: 0, x: 640, y: 380, z: 1, mz: 1.5 },
      { at: 3000, target: 'sc-card', z: 1.05, mz: 1.8 },
    ],
    captions: [
      { at: 0, text: { en: 'Open http://your-server:7300/ in your browser.', 'zh-CN': '在浏览器中打开 http://你的服务器:7300/。', 'zh-TW': '在瀏覽器中開啟 http://你的伺服器:7300/。', ko: '브라우저에서 http://내-서버:7300/을 엽니다.' } },
      { at: 3000, text: { en: 'Go through the wizard yourself: language, server name, client versions, client data, database and the administrator account.', 'zh-CN': '自己走完设置向导：语言、服务器名称、客户端版本、客户端数据、数据库和管理员账号。', 'zh-TW': '自己走完設定精靈：語言、伺服器名稱、用戶端版本、用戶端資料、資料庫和管理員帳號。', ko: '설정 마법사를 직접 진행하세요: 언어, 서버 이름, 클라이언트 버전, 클라이언트 데이터, 데이터베이스, 관리자 계정.' } },
      { at: 6400, text: { en: 'No database server? Choose SQLite: it needs nothing else.', 'zh-CN': '没有数据库服务器？选择 SQLite，不需要额外安装。', 'zh-TW': '沒有資料庫伺服器？選擇 SQLite，不需要額外安裝。', ko: '데이터베이스 서버가 없나요? SQLite를 선택하면 따로 설치할 것이 없습니다.' } },
    ],
  },
]

// The macOS wallpaper is artwork by pixiv@seamonsterping, credited under the player.
const credits = {
  linux: { en: 'Wallpaper: pixiv@seamonsterping', 'zh-CN': '壁纸：pixiv@seamonsterping', 'zh-TW': '桌布：pixiv@seamonsterping', ko: '배경화면: pixiv@seamonsterping' },
}

const tutorial: Tutorial = { commands: true, steps: linux, platforms: { linux, windows }, credits }

export default tutorial
