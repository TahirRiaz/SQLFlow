/**
 * A name cut in its middle. The flows of a folder share how their names begin and differ in how they end
 * (`...-01-pre`, `...-03-delivery`), and a kind's version is its last segment, so cutting the end would leave a
 * column of cards reading alike.
 */
export function clipMiddle(name: string, room: number): string {
  if (name.length <= room) {
    return name;
  }
  const tail = Math.floor((room - 1) / 2);
  const head = room - 1 - tail;
  return `${name.slice(0, head)}…${name.slice(name.length - tail)}`;
}
