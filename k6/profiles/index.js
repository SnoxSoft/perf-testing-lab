import smoke from './smoke.js';
import load from './load.js';

// Every profile, keyed by the name used with --env PROFILE=<name>. The k6
// equivalent of the IProfile[] array in the NBomber runner's Program.cs.
const PROFILES = [
  smoke,
  load,
];

export default PROFILES.reduce((map, profile) => {
  map[profile.name] = profile;
  return map;
}, {});
