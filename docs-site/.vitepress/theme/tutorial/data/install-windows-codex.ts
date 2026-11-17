import type { Localized, Tutorial } from '../types'
import { prompt, reply } from './install-common'

// Install Bubble Fighter Fantasy on Windows by Codex, the coding side of the ChatGPT desktop app.
// ChatGPT installs from the Microsoft Store. The app is drawn in each page language with its own
// interface wording; captions, the prompt and the agent's reply follow the page.

// Codex names a new chat after the first line of its first message; the views cut it to fit.
const taskTitle = Object.fromEntries(Object.entries(prompt).map(([code, text]) => [code, text.split('\n')[0]])) as Localized

const tutorial: Tutorial = {
  notice: {
    en: { title: 'Before you start', ok: 'OK', body: [
      'Codex usage counts against your ChatGPT plan, and an install like this one uses a fair share of it.',
      'If your plan does not include Codex or its usage is used up, upgrade or add credits first. Otherwise Codex cannot run.',
    ] },
    'zh-CN': { title: '开始之前', ok: '我知道了', body: [
      '在中国大陆，ChatGPT（包括其网站、桌面应用和 Codex）可能无法访问或使用受限，教程中用到的部分境外服务也可能无法打开。请先确认你的网络能正常使用这些服务；如果不能，建议改看 WorkBuddy 或 DeepSeek Harness 教程。',
      'Codex 的使用量计入你的 ChatGPT 套餐，像这样的安装任务会消耗不少额度。如果套餐不含 Codex 或额度已用完，请先升级套餐或购买额度，否则 Codex 无法运行。',
    ] },
    'zh-TW': { title: '開始之前', ok: '我知道了', body: [
      'Codex 的使用量計入你的 ChatGPT 方案，像這樣的安裝任務會消耗不少額度。',
      '如果方案不含 Codex 或額度已用完，請先升級方案或購買額度，否則 Codex 無法執行。',
    ] },
    ko: { title: '시작하기 전에', ok: '확인', body: [
      'Codex 사용량은 ChatGPT 요금제에서 차감되며, 이런 설치 작업은 사용량을 꽤 씁니다.',
      '요금제에 Codex가 없거나 사용량을 다 썼다면 먼저 요금제를 올리거나 크레딧을 추가하세요. 그렇지 않으면 Codex가 실행되지 않습니다.',
    ] },
  },
  prompt: {
    agent: 'Codex',
    step: 5,
    title: { en: 'The install prompt', 'zh-CN': '安装提示词', 'zh-TW': '安裝提示詞', ko: '설치 프롬프트' },
    note: {
      en: 'Copy it into Codex in step 6. It works with any agent that can run commands on Windows.',
      'zh-CN': '在第 6 步复制到 Codex。任何能在 Windows 上运行命令的智能体都可以使用。',
      'zh-TW': '在第 6 步複製到 Codex。任何能在 Windows 上執行命令的 AI 代理都可以使用。',
      ko: '6단계에서 Codex에 붙여 넣으세요. Windows에서 명령을 실행할 수 있는 다른 에이전트에서도 쓸 수 있습니다.',
    },
    text: prompt,
  },
  steps: [
    {
      id: 'download',
      scene: 'browser',
      props: { product: 'chatgpt' },
      title: { en: 'Search for ChatGPT and install it from the Microsoft Store', 'zh-CN': '搜索 ChatGPT，从 Microsoft Store 安装', 'zh-TW': '搜尋 ChatGPT，從 Microsoft Store 安裝', ko: 'ChatGPT를 검색하고 Microsoft Store에서 설치' },
      duration: 19000,
      cues: {
        open: 150, boxClick: 1500, typeStart: 1750, suggest: 1900, typeEnd: 2700, search: 3400, results: 3650,
        linkClick: 5300, site: 5600, dlClick: 8800, extPrompt: 9000, extPress: 10300, store: 10450,
        getPress: 12200, get: 12350, getDone: 16800,
      },
      cursor: [
        { at: 600, x: 760, y: 560 },
        { at: 1500, target: 'search-box', dx: -180, dy: -4, click: true, move: 800 },
        { at: 3400, target: 'search-go', click: true, move: 650 },
        { at: 5300, target: 'result-link', dx: -10, click: true, move: 900 },
        { at: 8800, target: 'site-download', click: true, move: 1100 },
        { at: 10300, target: 'ext-open', click: true, move: 900 },
        { at: 12200, target: 'store-get', dx: -30, click: true, move: 1200 },
        { at: 13600, x: 900, y: 560, move: 900 },
        { at: 17400, target: 'store-get', dx: 40, dy: 6, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 380, z: 1, mz: 1.5 },
        { at: 1200, target: 'search-box', z: 1.12, mz: 2.1 },
        { at: 3700, target: 'result-link', y: 360, z: 1, mz: 1.7 },
        { at: 5600, x: 640, y: 380, z: 1, mz: 1.5 },
        { at: 8100, target: 'site-download', z: 1.1, mz: 2 },
        { at: 9100, target: 'ext-open', y: 220, z: 1.15, mz: 2 },
        { at: 10600, x: 640, y: 380, z: 1, mz: 1.6 },
        { at: 11600, target: 'store-get', z: 1.15, mz: 2.1 },
      ],
      captions: [
        { at: 0, text: { en: 'Open a browser and go to Google.', 'zh-CN': '打开浏览器，进入 Google。', 'zh-TW': '開啟瀏覽器，進入 Google。', ko: '브라우저를 열고 Google에 들어갑니다.' } },
        { at: 1500, text: { en: 'Search for ChatGPT.', 'zh-CN': '搜索 ChatGPT。', 'zh-TW': '搜尋 ChatGPT。', ko: 'ChatGPT를 검색합니다.' } },
        { at: 3650, text: { en: 'Under the official chatgpt.com result, open Download.', 'zh-CN': '在 chatgpt.com 官方结果下打开“Download”（下载）。', 'zh-TW': '在 chatgpt.com 官方結果下開啟 Download（下載）。', ko: 'chatgpt.com 공식 결과 아래의 Download(다운로드)를 엽니다.' } },
        { at: 5600, text: { en: 'Click “Download for Windows”.', 'zh-CN': '点击“Download for Windows”。', 'zh-TW': '點選「Download for Windows」（下載 Windows 版）。', ko: '“Download for Windows”(Windows용 다운로드)를 클릭합니다.' } },
        { at: 9000, text: { en: 'ChatGPT installs through the Microsoft Store. Let the browser open it.', 'zh-CN': 'ChatGPT 通过 Microsoft Store 安装。允许浏览器打开它。', 'zh-TW': 'ChatGPT 透過 Microsoft Store 安裝。允許瀏覽器開啟它。', ko: 'ChatGPT는 Microsoft Store로 설치합니다. 브라우저가 스토어를 열도록 허용합니다.' } },
        { at: 10450, text: { en: 'The ChatGPT page opens in the Store. Click Get.', 'zh-CN': '商店里会打开 ChatGPT 页面。点击“获取”。', 'zh-TW': '商店裡會開啟 ChatGPT 頁面。點選「取得」。', ko: '스토어에서 ChatGPT 페이지가 열립니다. “받기”를 클릭합니다.' } },
        { at: 12350, text: { en: 'Wait until it is downloaded and installed; the button turns into Open.', 'zh-CN': '等待下载并安装完成，按钮会变成“打开”。', 'zh-TW': '等待下載並安裝完成，按鈕會變成「開啟」。', ko: '다운로드와 설치가 끝날 때까지 기다리면 버튼이 “열기”로 바뀝니다.' } },
      ],
    },
    {
      id: 'open',
      scene: 'codex',
      title: { en: 'Open ChatGPT', 'zh-CN': '打开 ChatGPT', 'zh-TW': '開啟 ChatGPT', ko: 'ChatGPT 열기' },
      duration: 6000,
      props: { phase: 'open' },
      cues: { open: 150 },
      cursor: [
        { at: 900, x: 760, y: 640 },
        { at: 3800, x: 700, y: 520, move: 1400 },
      ],
      camera: [
        { at: 0, x: 640, y: 380, z: 1, mz: 1.5 },
        { at: 1600, x: 700, y: 380, z: 1.06, mz: 1.8, dur: 1400 },
      ],
      captions: [
        { at: 0, text: { en: 'Start ChatGPT. Sign in first if it asks you to.', 'zh-CN': '启动 ChatGPT。如果要求登录，请先登录。', 'zh-TW': '啟動 ChatGPT。如果要求登入，請先登入。', ko: 'ChatGPT를 시작합니다. 로그인을 요청하면 먼저 로그인합니다.' } },
        { at: 2400, text: { en: 'The first time, it opens on the ChatGPT chat. Codex, the coding agent, is in the same app.', 'zh-CN': '第一次打开时显示 ChatGPT 聊天。编程智能体 Codex 就在同一个应用里。', 'zh-TW': '第一次開啟時顯示 ChatGPT 聊天。程式設計 AI 代理 Codex 就在同一個應用程式裡。', ko: '처음에는 ChatGPT 채팅이 열립니다. 코딩 에이전트인 Codex도 같은 앱에 있습니다.' } },
      ],
    },
    {
      id: 'switch',
      scene: 'codex',
      title: { en: 'Switch from ChatGPT to Codex', 'zh-CN': '从 ChatGPT 切换到 Codex', 'zh-TW': '從 ChatGPT 切換到 Codex', ko: 'ChatGPT에서 Codex로 전환' },
      duration: 7000,
      props: { phase: 'switch' },
      cues: { menu: 1500, hoverCodex: 2700, pressCodex: 3000, switch: 3150 },
      cursor: [
        { at: 300, x: 700, y: 420 },
        { at: 1400, target: 'cx-app', dx: -10, click: true, move: 900 },
        { at: 2700, target: 'cx-codex', dx: -30, move: 700 },
        { at: 3000, target: 'cx-codex', dx: -30, click: true, move: 150 },
        { at: 4400, x: 760, y: 520, move: 1100 },
      ],
      camera: [
        { at: 0, x: 640, y: 380, z: 1, mz: 1.6 },
        { at: 900, target: 'cx-app', x: 420, y: 230, z: 1.25, mz: 2.3 },
        { at: 3600, x: 700, y: 420, z: 1, mz: 1.6, dur: 1200 },
      ],
      captions: [
        { at: 0, text: { en: 'Open the ChatGPT menu at the top of the sidebar.', 'zh-CN': '打开侧边栏顶部的 ChatGPT 菜单。', 'zh-TW': '開啟側邊欄頂端的 ChatGPT 選單。', ko: '사이드바 맨 위의 ChatGPT 메뉴를 엽니다.' } },
        { at: 2600, text: { en: 'Choose Codex (Build, debug, and ship).', 'zh-CN': '选择 Codex（构建、调试和发布）。', 'zh-TW': '選擇 Codex（建置、偵錯與發布）。', ko: 'Codex(빌드하고, 디버깅하고, 배포하세요)를 선택합니다.' } },
        { at: 3600, text: { en: 'Codex opens. It works in projects: folders on this computer.', 'zh-CN': 'Codex 打开了。它在项目中工作，项目就是这台电脑上的文件夹。', 'zh-TW': 'Codex 開啟了。它在專案中工作，專案就是這台電腦上的資料夾。', ko: 'Codex가 열립니다. Codex는 프로젝트, 즉 이 컴퓨터의 폴더에서 작업합니다.' } },
      ],
    },
    {
      id: 'workspace',
      scene: 'codex',
      title: { en: 'Create a project for the server folder', 'zh-CN': '为服务器文件夹创建项目', 'zh-TW': '為伺服器資料夾建立專案', ko: '서버 폴더용 프로젝트 만들기' },
      duration: 17000,
      props: { phase: 'workspace' },
      cues: {
        projMenu: 1400, newHover: 2500, dialog: 2900, nameFocus: 3300, nameStart: 3500, nameEnd: 5000, addPress: 5900, picker: 6050,
        newPress: 7100, newFolder: 7250, fnStart: 7500, fnEnd: 8800, okPress: 9900, pickerClose: 10050,
        createPress: 11300, trust: 11450, trustPress: 13300, ready: 13450,
      },
      cursor: [
        { at: 300, x: 700, y: 640 },
        { at: 1300, target: 'cx-project', dx: -20, click: true, move: 900 },
        { at: 2800, target: 'cx-newproj', dx: -30, click: true, move: 900 },
        { at: 3300, target: 'cx-name', dx: -60, click: true, move: 500 },
        { at: 5900, target: 'cx-add', click: true, move: 1000 },
        { at: 7100, target: 'fd-new', click: true, move: 1000 },
        { at: 9000, target: 'fd-newrow', dx: -150, move: 700 },
        { at: 9900, target: 'fd-ok', click: true, move: 800 },
        { at: 11300, target: 'cx-create', click: true, move: 1000 },
        { at: 13300, target: 'cx-trust', click: true, move: 1100 },
        { at: 14700, target: 'cx-project', dx: 70, dy: 20, move: 900 },
      ],
      camera: [
        { at: 0, x: 680, y: 520, z: 1, mz: 1.6 },
        { at: 900, target: 'cx-project', y: 560, z: 1.15, mz: 2.2 },
        { at: 3000, x: 640, y: 380, z: 1.1, mz: 1.8 },
        { at: 6200, x: 610, y: 300, z: 1, mz: 1.5 },
        { at: 6800, target: 'fd-new', y: 200, z: 1.1, mz: 2.2 },
        { at: 9500, target: 'fd-ok', y: 420, z: 1.05, mz: 2 },
        { at: 10200, x: 640, y: 380, z: 1.1, mz: 1.8 },
        { at: 13700, x: 720, y: 440, z: 1, mz: 1.6 },
      ],
      captions: [
        { at: 0, text: { en: 'Click “Choose project” above the chat box.', 'zh-CN': '点击对话框上方的“选择项目”。', 'zh-TW': '點選對話框上方的「選擇專案」。', ko: '채팅 상자 위의 “프로젝트 선택”을 클릭합니다.' } },
        { at: 2500, text: { en: 'Choose “New project” and name it, for example BubbleFighterFantasy.', 'zh-CN': '选择“新建项目”，起个名字，例如 BubbleFighterFantasy。', 'zh-TW': '選擇「新專案」，取個名稱，例如 BubbleFighterFantasy。', ko: '“새 프로젝트”를 선택하고 BubbleFighterFantasy 같은 이름을 붙입니다.' } },
        { at: 5800, text: { en: 'Click Add, then create and select an empty folder for the server.', 'zh-CN': '点击“添加”，新建并选择一个用于服务器的空文件夹。', 'zh-TW': '點選「新增」，建立並選擇一個用於伺服器的空資料夾。', ko: '“추가”를 클릭하고 서버용 빈 폴더를 만들어 선택합니다.' } },
        { at: 10100, text: { en: 'Tip: with more than one folder, a Primary button marks the main one. Keep the server folder as Primary.', 'zh-CN': '提示：添加多个文件夹时会出现“主要”按钮，表示主文件夹。把服务器文件夹设为主要。', 'zh-TW': '提示：新增多個資料夾時會出現「主要」按鈕，表示主要資料夾。把伺服器資料夾設為主要。', ko: '팁: 폴더를 여러 개 추가하면 기본 폴더를 표시하는 “기본” 버튼이 나타납니다. 서버 폴더를 기본으로 두세요.' } },
        { at: 11300, text: { en: 'Create the project and trust the folder, so Codex can read, edit and run files in it.', 'zh-CN': '创建项目并信任这个文件夹，Codex 才能在其中读取、编辑和运行文件。', 'zh-TW': '建立專案並信任這個資料夾，Codex 才能在其中讀取、編輯和執行檔案。', ko: '프로젝트를 만들고 폴더를 신뢰해야 Codex가 그 안의 파일을 읽고 수정하고 실행할 수 있습니다.' } },
        { at: 13700, text: { en: 'The agent works in this folder. The server itself goes to C:\\BubbleFighterFantasy, a short path Windows accepts.', 'zh-CN': '智能体在这个文件夹里工作，服务器本身会安装到 C:\\BubbleFighterFantasy（Windows 能接受的短路径）。', 'zh-TW': 'AI 代理在這個資料夾裡工作，伺服器本身會安裝到 C:\\BubbleFighterFantasy（Windows 能接受的短路徑）。', ko: '에이전트는 이 폴더에서 작업합니다. 서버 자체는 Windows가 허용하는 짧은 경로인 C:\\BubbleFighterFantasy에 설치됩니다.' } },
      ],
    },
    {
      id: 'permission',
      scene: 'codex',
      title: { en: 'Change the permission to full access', 'zh-CN': '把权限改为完全访问', 'zh-TW': '把權限改為完整存取權', ko: '권한을 전체 액세스로 변경' },
      duration: 9000,
      props: { phase: 'permission' },
      cues: { permMenu: 1400, permHover: 2500, permDialog: 2900, confirmPress: 5700, allow: 5850 },
      cursor: [
        { at: 300, x: 720, y: 600 },
        { at: 1300, target: 'cx-perm', dx: -10, click: true, move: 900 },
        { at: 2800, target: 'cx-perm-full', dx: -60, click: true, move: 800 },
        { at: 5700, target: 'cx-confirm', click: true, move: 1400 },
        { at: 7000, target: 'cx-perm', dx: 70, dy: 30, move: 900 },
      ],
      camera: [
        { at: 0, x: 700, y: 560, z: 1, mz: 1.6 },
        { at: 800, target: 'cx-perm', x: 760, y: 600, z: 1.15, mz: 2.2 },
        { at: 3000, x: 640, y: 380, z: 1.05, mz: 1.7 },
        { at: 6100, target: 'cx-perm', x: 760, y: 600, z: 1.15, mz: 2.2 },
      ],
      captions: [
        { at: 0, text: { en: 'Open the permission menu in the chat box.', 'zh-CN': '打开对话框里的权限菜单。', 'zh-TW': '開啟對話框裡的權限選單。', ko: '채팅 상자의 권한 메뉴를 엽니다.' } },
        { at: 2400, text: { en: 'Choose Full access.', 'zh-CN': '选择“完全访问”。', 'zh-TW': '選擇「完整存取權」。', ko: '“전체 액세스”를 선택합니다.' } },
        { at: 3000, text: { en: 'With full access Codex runs commands and changes files without asking each time. Use it only in the folder you just made.', 'zh-CN': '开启后，Codex 运行命令和修改文件时不再每次询问。只在刚才新建的文件夹里使用。', 'zh-TW': '開啟後，Codex 執行命令和修改檔案時不再每次詢問。只在剛才新增的資料夾裡使用。', ko: '전체 액세스를 켜면 Codex가 매번 묻지 않고 명령을 실행하고 파일을 바꿉니다. 방금 만든 폴더에서만 사용하세요.' } },
        { at: 5000, text: { en: 'Confirm it.', 'zh-CN': '点击“确认”。', 'zh-TW': '點選「確認」。', ko: '“확인”을 클릭합니다.' } },
        { at: 6200, text: { en: 'The chat box now shows Full access.', 'zh-CN': '对话框现在显示“完全访问”。', 'zh-TW': '對話框現在顯示「完整存取權」。', ko: '이제 채팅 상자에 “전체 액세스”가 표시됩니다.' } },
      ],
    },
    {
      id: 'install',
      scene: 'codex',
      title: { en: 'Send the prompt; Codex installs the server', 'zh-CN': '发送提示词，Codex 为你安装服务器', 'zh-TW': '傳送提示詞，Codex 為你安裝伺服器', ko: '프롬프트를 보내면 Codex가 서버를 설치' },
      duration: 34000,
      props: { phase: 'chat', prompt, reply: reply(13000), taskTitle },
      cues: { focus: 1000, typeStart: 1200, typeEnd: 8200, sendPress: 9100, send: 9250, chat: 10300, think: 11300, done: 31600 },
      cursor: [
        { at: 300, x: 760, y: 560 },
        { at: 1000, target: 'composer', dx: -200, dy: -10, click: true, move: 700 },
        { at: 2400, x: 1000, y: 600, move: 900 },
        { at: 9100, target: 'send', click: true, move: 900 },
        { at: 10400, x: 1100, y: 720, move: 900 },
        { at: 10800, x: 1100, y: 720, hide: true },
      ],
      camera: [
        { at: 0, target: 'composer', y: 520, z: 1.1, mz: 2 },
        { at: 8500, target: 'send', y: 520, z: 1.1, mz: 2 },
        { at: 9600, x: 760, y: 340, z: 1.04, mz: 1.8 },
        { at: 14000, x: 760, y: 380, z: 1.04, mz: 1.8 },
      ],
      captions: [
        { at: 0, text: { en: 'Click the chat box and paste the install prompt (copy it below the player).', 'zh-CN': '点击对话框，粘贴安装提示词（在播放器下方复制）。', 'zh-TW': '點選對話框，貼上安裝提示詞（在播放器下方複製）。', ko: '채팅 상자를 클릭하고 설치 프롬프트를 붙여 넣습니다(플레이어 아래에서 복사).' } },
        { at: 8600, text: { en: 'Send it.', 'zh-CN': '发送。', 'zh-TW': '傳送。', ko: '보냅니다.' } },
        { at: 9300, text: { en: 'Codex starts the chat, thinks, then gets to work.', 'zh-CN': 'Codex 开始对话、思考，然后开始工作。', 'zh-TW': 'Codex 開始對話、思考，然後開始工作。', ko: 'Codex가 대화를 시작하고 생각한 뒤 작업에 들어갑니다.' } },
        { at: 14000, text: { en: 'It reads the deployment guide, then runs the project\'s install script, which downloads and verifies the package and installs the runtime when it is missing.', 'zh-CN': '它先阅读部署指南，再运行项目自带的安装脚本：脚本会下载并校验安装包，缺少运行时时自动安装。', 'zh-TW': '它先閱讀部署指南，再執行專案內建的安裝腳本：腳本會下載並校驗安裝套件，缺少執行階段時自動安裝。', ko: '배포 가이드를 읽은 뒤 프로젝트의 설치 스크립트를 실행합니다. 스크립트가 패키지를 내려받아 검증하고, 런타임이 없으면 설치합니다.' } },
        { at: 23200, text: { en: 'The script starts the server in its own window and waits until http://127.0.0.1:7300/health answers ok.', 'zh-CN': '脚本在单独的窗口中启动服务器，并等待 http://127.0.0.1:7300/health 返回 ok。', 'zh-TW': '腳本在獨立的視窗中啟動伺服器，並等待 http://127.0.0.1:7300/health 回傳 ok。', ko: '스크립트가 서버를 별도 창에서 시작하고 http://127.0.0.1:7300/health가 ok를 반환할 때까지 기다립니다.' } },
        { at: 27100, text: { en: 'It ends with a Markdown summary. Open http://127.0.0.1:7300/ to finish the setup assistant.', 'zh-CN': '最后用 Markdown 总结。打开 http://127.0.0.1:7300/ 完成设置向导。', 'zh-TW': '最後用 Markdown 總結。開啟 http://127.0.0.1:7300/ 完成設定精靈。', ko: '마지막으로 Markdown 요약을 보여 줍니다. http://127.0.0.1:7300/을 열어 설정 도우미를 마치세요.' } },
      ],
    },
  ],
}

export default tutorial
