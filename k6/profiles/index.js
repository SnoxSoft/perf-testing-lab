import smoke from './smoke.js';
import load from './load.js';
import { capacityPool, capacityLock, ceiling } from './capacity.js';
import stress from './stress.js';
import spike from './spike.js';
import endurance from './endurance.js';
import correlation from './correlation.js';

// Every profile, keyed by the name used with --env PROFILE=<name>. The k6
// equivalent of the IProfile[] array in the NBomber runner's Program.cs.
const PROFILES = [
  smoke,
  load,
  capacityPool,
  capacityLock,
  ceiling,
  stress,
  spike,
  endurance,
  correlation,
];

export default PROFILES.reduce((map, profile) => {
  map[profile.name] = profile;
  return map;
}, {});
