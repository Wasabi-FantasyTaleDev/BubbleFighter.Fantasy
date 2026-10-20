// Profile data owns version names; configuration ids remain stable when the UI language changes.
export function clientName(profiles, id, language) {
  const profile = profiles.find(candidate => candidate.id === id)
  return profile?.displayNames?.[language] || profile?.displayNames?.en || id
}
