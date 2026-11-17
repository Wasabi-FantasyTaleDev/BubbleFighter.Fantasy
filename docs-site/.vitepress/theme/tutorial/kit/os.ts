import type { Code } from '../types'

// Windows, Explorer and browser wording in the reader's language, so a drawn desktop matches the
// system a reader of that page would see. Product interfaces keep their own wording in the scenes.

const zh = {
  storeHome: '主页', storeApps: '应用', storeGaming: '游戏', storeLibrary: '库', storeGet: '获取', storeOpen: '打开', storeRatings: (n: number) => `${n} 个评级`,
  storeDownloading: '正在下载', storeInstalling: '正在安装', storeShots: '屏幕截图', storeSearch: '搜索应用、游戏、电影及更多内容',
  extTitle: (app: string) => `要打开 ${app} 吗？`, extBody: (site: string) => `${site} 想打开此应用。`, extOpen: (app: string) => `打开 ${app}`,
  search: '搜索', ime: '中', settingsTab: '设置', downloads: '下载', seeMore: '查看更多',
  keepWarn: (file: string) => `${file} 可能会损害你的设备。是否仍要保留？`, keep: '保留', remove: '删除',
  progress: (got: string, total: number, secs: number) => `9.4 MB/s - ${got} MB/${total} MB，剩余 ${secs} 秒`,
  openFile: '打开文件', create: '新建', sort: '排序', view: '查看',
  home: '主文件夹', desktop: '桌面', documents: '文档', pictures: '图片', music: '音乐', videos: '视频', thisPc: '此电脑', network: '网络',
  searchIn: (place: string) => `在 ${place} 中搜索`, name: '名称', modified: '修改日期', type: '类型', size: '大小', today: '今天',
  app: '应用程序', folder: '文件夹', items: (n: number) => `${n} 个项目`,
  fileDesc: '文件说明', fileVersion: '文件版本',
  pickTitle: '选择文件夹', organize: '组织', newFolder: '新建文件夹', folderLabel: '文件夹：', pickOk: '选择文件夹', cancel: '取消',
  installing: (app: string) => `正在安装 ${app}…`, installed: '安装完成', installedHint: (app: string) => `可以在开始菜单或桌面打开 ${app}。`, setup: (app: string) => `安装 ${app}`,
  stamp: '2026/11/16 16:07', newStamp: '2026/11/16 16:12', oldStamp: '2026/10/20 18:09', clock: '16:07', date: '2026/11/16',
}
type Os = typeof zh

const tw: Os = {
  storeHome: '首頁', storeApps: '應用程式', storeGaming: '遊戲', storeLibrary: '媒體櫃', storeGet: '取得', storeOpen: '開啟', storeRatings: n => `${n} 個評分`,
  storeDownloading: '正在下載', storeInstalling: '正在安裝', storeShots: '螢幕擷取畫面', storeSearch: '搜尋應用程式、遊戲、電影等',
  extTitle: app => `要開啟「${app}」嗎？`, extBody: site => `${site} 要開啟這個應用程式。`, extOpen: app => `開啟「${app}」`,
  search: '搜尋', ime: '中', settingsTab: '設定', downloads: '下載', seeMore: '查看更多',
  keepWarn: file => `${file} 可能會損害你的裝置。是否仍要保留？`, keep: '保留', remove: '刪除',
  progress: (got, total, secs) => `9.4 MB/s - ${got} MB/${total} MB，剩餘 ${secs} 秒`,
  openFile: '開啟檔案', create: '新增', sort: '排序', view: '檢視',
  home: '常用', desktop: '桌面', documents: '文件', pictures: '圖片', music: '音樂', videos: '影片', thisPc: '本機', network: '網路',
  searchIn: place => `搜尋 ${place}`, name: '名稱', modified: '修改日期', type: '類型', size: '大小', today: '今天',
  app: '應用程式', folder: '檔案資料夾', items: n => `${n} 個項目`,
  fileDesc: '檔案描述', fileVersion: '檔案版本',
  pickTitle: '選擇資料夾', organize: '組合管理', newFolder: '新增資料夾', folderLabel: '資料夾：', pickOk: '選擇資料夾', cancel: '取消',
  installing: app => `正在安裝 ${app}…`, installed: '安裝完成', installedHint: app => `可以在開始功能表或桌面開啟 ${app}。`, setup: app => `安裝 ${app}`,
  stamp: '2026/11/16 下午 04:07', newStamp: '2026/11/16 下午 04:12', oldStamp: '2026/10/20 下午 06:09', clock: '下午 04:07', date: '2026/11/16',
}

const en: Os = {
  storeHome: 'Home', storeApps: 'Apps', storeGaming: 'Gaming', storeLibrary: 'Library', storeGet: 'Get', storeOpen: 'Open', storeRatings: n => `${n} ratings`,
  storeDownloading: 'Downloading', storeInstalling: 'Installing', storeShots: 'Screenshots', storeSearch: 'Search apps, games, movies and more',
  extTitle: app => `Open ${app}?`, extBody: site => `${site} wants to open this application.`, extOpen: app => `Open ${app}`,
  search: 'Search', ime: 'ENG', settingsTab: 'Settings', downloads: 'Downloads', seeMore: 'See more',
  keepWarn: file => `${file} may harm your device. Do you want to keep it anyway?`, keep: 'Keep', remove: 'Delete',
  progress: (got, total, secs) => `9.4 MB/s - ${got} MB of ${total} MB, ${secs} secs left`,
  openFile: 'Open file', create: 'New', sort: 'Sort', view: 'View',
  home: 'Home', desktop: 'Desktop', documents: 'Documents', pictures: 'Pictures', music: 'Music', videos: 'Videos', thisPc: 'This PC', network: 'Network',
  searchIn: place => `Search ${place}`, name: 'Name', modified: 'Date modified', type: 'Type', size: 'Size', today: 'Today',
  app: 'Application', folder: 'File folder', items: n => `${n} item${n === 1 ? '' : 's'}`,
  fileDesc: 'File description', fileVersion: 'File version',
  pickTitle: 'Select Folder', organize: 'Organize', newFolder: 'New folder', folderLabel: 'Folder:', pickOk: 'Select Folder', cancel: 'Cancel',
  installing: app => `Installing ${app}…`, installed: 'Installation complete', installedHint: app => `Open ${app} from the Start menu or the desktop.`, setup: app => `${app} Setup`,
  stamp: '11/16/2026 4:07 PM', newStamp: '11/16/2026 4:12 PM', oldStamp: '10/20/2026 6:09 PM', clock: '4:07 PM', date: '11/16/2026',
}

const ko: Os = {
  storeHome: '홈', storeApps: '앱', storeGaming: '게임', storeLibrary: '라이브러리', storeGet: '받기', storeOpen: '열기', storeRatings: n => `평가 ${n}개`,
  storeDownloading: '다운로드 중', storeInstalling: '설치 중', storeShots: '스크린샷', storeSearch: '앱, 게임, 영화 등 검색',
  extTitle: app => `${app}을(를) 여시겠습니까?`, extBody: site => `${site}에서 이 애플리케이션을 열려고 합니다.`, extOpen: app => `${app} 열기`,
  search: '검색', ime: '한', settingsTab: '설정', downloads: '다운로드', seeMore: '더보기',
  keepWarn: file => `${file} 파일이 장치에 해를 끼칠 수 있습니다. 그래도 유지하시겠습니까?`, keep: '유지', remove: '삭제',
  progress: (got, total, secs) => `9.4 MB/s - ${got} MB/${total} MB, ${secs}초 남음`,
  openFile: '파일 열기', create: '새로 만들기', sort: '정렬', view: '보기',
  home: '홈', desktop: '바탕 화면', documents: '문서', pictures: '사진', music: '음악', videos: '동영상', thisPc: '내 PC', network: '네트워크',
  searchIn: place => `${place} 검색`, name: '이름', modified: '수정한 날짜', type: '유형', size: '크기', today: '오늘',
  app: '응용 프로그램', folder: '파일 폴더', items: n => `${n}개 항목`,
  fileDesc: '파일 설명', fileVersion: '파일 버전',
  pickTitle: '폴더 선택', organize: '구성', newFolder: '새 폴더', folderLabel: '폴더:', pickOk: '폴더 선택', cancel: '취소',
  installing: app => `${app} 설치 중…`, installed: '설치 완료', installedHint: app => `시작 메뉴나 바탕 화면에서 ${app}을(를) 여세요.`, setup: app => `${app} 설치`,
  stamp: '2026-11-16 오후 4:07', newStamp: '2026-11-16 오후 4:12', oldStamp: '2026-10-20 오후 6:09', clock: '오후 4:07', date: '2026-11-16',
}

const table: Record<Code, Os> = { 'zh-CN': zh, 'zh-TW': tw, en, ko }
export const osText = (lang: Code) => table[lang] ?? en
export type { Os }
