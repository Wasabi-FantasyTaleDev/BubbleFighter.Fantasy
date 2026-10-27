import clientBuilds from './clientBuilds'

// Development-only room records for the directory and player drawer.
const nicknames = ['Wasabi', 'Philosophyz', 'TrustInMe', 'FakeMe', 'FindTheBlue', 'RaisedBed', 'PottedOne', 'CANOE', 'Remembrance']
const titles = ['难道无人能赐我一败？', '逃跑达人宝宝', '让你看看俺躲的最高境界', '酣畅淋漓的一局！', '泡泡战士总攻']
const builds = ['kr-1256', 'cn-774', 'kr-1256', 'tw-454', 'kr-812']
const levels = [32, 26, 18, 21, 45, 12, 38, 54, 63]
const counts = [4, 2, 4, 4, 1]

export default titles.map((title, index) => {
  const players = Array.from({ length: counts[index] }, (_, seat) => {
    const person = (index + seat) % nicknames.length
    return { id: `room-${index}-seat-${seat}`, nickname: nicknames[person], level: levels[person],
      team: seat % 2 ? 'blue' : 'red', host: seat === 0, ready: seat !== 3 }
  })
  return { roomId: 1001 + index, title, modeKey: index === 4 ? 'rooms.mode.team' : 'rooms.mode.classic',
    mapKey: 'rooms.map.random', owner: players[0], players, capacity: index === 3 ? 4 : 8,
    locked: index === 1, ...(index === 1 ? { password: '2468' } : {}),
    phase: index === 2 || index === 3 ? 'Playing' : 'Waiting',
    clientBuild: clientBuilds.find(build => build.id === builds[index]) }
})
