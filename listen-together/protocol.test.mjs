import test from 'node:test';
import assert from 'node:assert/strict';
import { parseInvitation, targetSeconds } from './protocol.mjs';

const invite = 'orpheus://nm/multiListenTogether/joinRoom?roomId=test_room_123&inviterId=12345';
test('actual two-level private-message wrapper keeps room and inviter', () => {
  const wrap = u => 'orpheus://open?url1=' + encodeURIComponent(u) + '&url2=' + encodeURIComponent('https://st.music.163.com/app-upgrade/');
  assert.deepEqual(parseInvitation(wrap(wrap(invite))), { roomId: 'test_room_123', inviterUid: '12345' });
});
test('official multiplayer web invitation and escaped separators', () => {
  assert.deepEqual(parseInvitation('https://st.music.163.com/listen-together/multishare/index.html?roomId=test_room_123&amp;inviterUid=12345\\&isFLT=false'), { roomId: 'test_room_123', inviterUid: '12345' });
});
test('reject unrelated, ambiguous, follow-listen and excessive nesting', () => {
  for (const value of [invite.replace('multiListenTogether/joinRoom', 'play/listenTogether'), invite + '&roomId=another', invite.replace('12345', '0'), 'https://example.org/listen-together/multishare/index.html?roomId=test&inviterUid=12', 'https://st.music.163.com/listen-together/multishare/index.html?roomId=test&inviterUid=12&isFLT=true']) assert.throws(() => parseInvitation(value));
  let deep = invite;
  for (let i = 0; i < 6; i++) deep = 'orpheus://open?url1=' + encodeURIComponent(deep);
  assert.throws(() => parseInvitation(deep));
});
test('progress uses milliseconds, elapsed sample time and end clamp', () => {
  assert.equal(targetSeconds({ playedTime: 12000, songDuration: 60000 }, 1000, 3000), 14);
  assert.equal(targetSeconds({ playedTime: 59900, songDuration: 60000 }, 1000, 3000), 59.75);
  assert.throws(() => targetSeconds({ playedTime: NaN, songDuration: 60000 }, 0, 1));
});
