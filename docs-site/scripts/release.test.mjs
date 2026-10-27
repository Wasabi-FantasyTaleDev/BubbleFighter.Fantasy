import assert from 'node:assert/strict'
import test from 'node:test'
import { windowsX64Asset } from '../.vitepress/theme/release.ts'

const release = (...names) => ({ assets: names.map(name => ({ name, url: `https://example.org/${name}` })) })

test('Windows downloads prefer a Windows package over Linux or an unqualified x64 archive', () => {
  const value = release('launcher-linux-x64.zip', 'launcher-x64.zip', 'launcher-win-x64.zip')
  assert.equal(windowsX64Asset(value)?.name, 'launcher-win-x64.zip')
})

test('foreign platforms and incompatible architectures are never offered as Windows x64', () => {
  for (const name of ['launcher-linux-x64.zip', 'launcher-darwin-x64.zip', 'launcher-macos-amd64.zip',
    'launcher-freebsd-x64.zip', 'launcher-mac-x64.zip', 'launcher-ios-x64.zip', 'launcher-win-arm64.zip', 'launcher-win-aarch64.zip',
    'launcher-win-armv7.zip', 'launcher-win32.exe', 'launcher-win-ia32.exe', 'launcher-win-x86.msi']) {
    assert.equal(windowsX64Asset(release(name)), undefined, name)
  }
})

test('supported Windows naming and x86_64 aliases retain their original URLs', () => {
  for (const name of ['launcher-win-x64.zip', 'launcher-Windows-AMD64.msi', 'launcher-win-x86_64.exe',
    'launcher-win-x86-64.zip', 'launcher-windows.zip', 'launcher-x64.zip']) {
    const value = release(name)
    assert.equal(windowsX64Asset(value), value.assets[0], name)
  }
})

test('missing releases, source archives and checksum assets remain unavailable', () => {
  assert.equal(windowsX64Asset(undefined), undefined)
  assert.equal(windowsX64Asset(release()), undefined)
  assert.equal(windowsX64Asset(release('source.zip', 'launcher.zip', 'launcher-win-x64.zip.sha256')), undefined)
})
